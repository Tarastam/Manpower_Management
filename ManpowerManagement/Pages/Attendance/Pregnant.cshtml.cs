using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Pages.Attendance;

public class PregnantModel(AppDbContext db, LeaderSessionService leaderSessions, AttendanceStagingStore staging) : PageModel
{
    LeaderSession? leader;
    string Actor => leader?.Actor ?? User.Identity?.Name ?? "Supervisor";

    // Same gate as the By Shift page: a checked-in leader or a signed-in Admin.
    public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context, Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        leader = await leaderSessions.ReadAsync(HttpContext, db);
        if (leader is null && !User.IsInRole(Roles.Admin)) { context.Result = RedirectToPage("CheckIn", new { returnPage = "Pregnant" }); return; }
        await next();
    }

    public const string GroupName = "Pregnant";
    public const string ShiftKey = "Pregnant";
    public const decimal StandardHours = 8m;
    public const decimal MaxWeeklyHours = 48m;

    [BindProperty(SupportsGet = true)] public string StartMonth { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string EndMonth { get; set; } = "";
    public List<IndexModel.GridRow> GridRows { get; set; } = [];
    public List<DateOnly> Days { get; set; } = [];
    public List<IndexModel.WeekGroup> WeekGroups { get; set; } = [];
    public string? Error { get; set; }
    public bool GroupMissing { get; set; }

    public async Task OnGetAsync()
    {
        SetCalendarRange();
        var employees = await db.Employees
            .Where(e => e.EmploymentState == EmploymentState.Active && e.SpecialGroups.Any(g => g.SpecialGroup!.Name == GroupName))
            .OrderBy(e => e.FullName).ToListAsync();
        if (!await db.SpecialGroups.AnyAsync(g => g.Name == GroupName)) { GroupMissing = true; }
        var employeeIds = employees.Select(e => e.Id).ToHashSet();
        var first = Days.First(); var last = Days.Last();
        var entries = await db.AttendanceEntries
            .Where(e => e.Shift == ShiftKey && e.IsConfirmed && e.BusinessDate >= first && e.BusinessDate <= last && employeeIds.Contains(e.EmployeeId))
            .ToDictionaryAsync(e => (e.EmployeeId, e.BusinessDate));
        // Changes still waiting for the 07:00 / 19:00 copy to SQL cover what is stored.
        foreach (var change in staging.PendingCells(c => c.Shift == ShiftKey && c.Date >= first && c.Date <= last && employeeIds.Contains(c.EmployeeId)))
        {
            if (change.Revert) { entries.Remove((change.EmployeeId, change.Date)); continue; }
            entries[(change.EmployeeId, change.Date)] = new AttendanceEntry { EmployeeId = change.EmployeeId, BusinessDate = change.Date, Shift = ShiftKey, WorkshopId = change.WorkshopId, WorkingHours = change.Hours, LeaveType = (LeaveType)change.LeaveType, LeaveHours = change.LeaveHours, IsConfirmed = true };
        }
        var offDays = Days.Where(IsDefaultOffDay).ToHashSet();
        GridRows = employees.Select(employee => new IndexModel.GridRow
        {
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeId,
            EmployeeName = employee.FullName,
            EmployeeShift = employee.Shift,
            Process = employee.Process,
            Cells = Days.Select(day =>
            {
                var isOffDay = offDays.Contains(day);
                if (isOffDay)
                {
                    return entries.TryGetValue((employee.Id, day), out var overtimeEntry)
                        ? new IndexModel.AttendanceCell { BusinessDate = day, WorkingHours = overtimeEntry.WorkingHours, WorkshopId = employee.CurrentWorkshopId, IsOffDay = true, IsConfirmed = overtimeEntry.IsConfirmed }
                        : new IndexModel.AttendanceCell { BusinessDate = day, WorkshopId = employee.CurrentWorkshopId, WorkingHours = 0, IsOffDay = true };
                }
                return entries.TryGetValue((employee.Id, day), out var entry)
                    ? new IndexModel.AttendanceCell { BusinessDate = day, WorkingHours = entry.WorkingHours, WorkshopId = employee.CurrentWorkshopId, LeaveType = entry.LeaveType, LeaveHours = entry.LeaveHours, IsConfirmed = entry.IsConfirmed }
                    : new IndexModel.AttendanceCell { BusinessDate = day, WorkshopId = employee.CurrentWorkshopId, WorkingHours = StandardHours };
            }).ToList()
        }).ToList();
    }

    // Saves one day. The change waits in the staging file and is copied to SQL at 07:00 / 19:00.
    public async Task<IActionResult> OnPostCellAsync(int employeeId, DateOnly businessDate, decimal workingHours, LeaveType leaveType, decimal leaveHours)
    {
        var today = BusinessDateService.Current();
        if (!User.IsInRole(Roles.Admin) && businessDate < today) return new JsonResult(new { ok = false, error = "Only Admin can edit a previous business date." });

        var employee = await db.Employees.FindAsync(employeeId);
        if (employee == null) return new JsonResult(new { ok = false, error = "Employee not found." });

        var isOffDay = IsDefaultOffDay(businessDate);
        if (isOffDay && (leaveType != LeaveType.None || leaveHours != 0)) return new JsonResult(new { ok = false, error = "OFF day entries can contain working hours only." });
        if (workingHours < 0 || leaveHours < 0 || workingHours + leaveHours > StandardHours) return new JsonResult(new { ok = false, error = $"Hours must total at most {StandardHours}." });

        // The week total counts everyone's default hours too, so use what each day currently holds (pending change, stored entry, or default).
        var weekStart = businessDate.AddDays(-(int)businessDate.DayOfWeek);
        var weekEnd = weekStart.AddDays(6);
        var stored = await db.AttendanceEntries
            .Where(e => e.EmployeeId == employeeId && e.Shift == ShiftKey && e.IsConfirmed && e.BusinessDate >= weekStart && e.BusinessDate <= weekEnd).ToDictionaryAsync(e => e.BusinessDate);
        var pending = staging.PendingCells(c => c.EmployeeId == employeeId && c.Shift == ShiftKey && c.Date >= weekStart && c.Date <= weekEnd).ToDictionary(c => c.Date);
        decimal DefaultHours(DateOnly day) => IsDefaultOffDay(day) ? 0 : StandardHours;
        decimal HoursOn(DateOnly day) => pending.TryGetValue(day, out var p) ? (p.Revert ? DefaultHours(day) : p.Hours) : stored.TryGetValue(day, out var e) ? e.WorkingHours : DefaultHours(day);
        var weeklyHours = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).Where(day => day != businessDate).Sum(HoursOn);
        if (weeklyHours + workingHours > MaxWeeklyHours) return new JsonResult(new { ok = false, error = $"Weekly working hours cannot exceed {MaxWeeklyHours:0} hours." });

        var isDefault = leaveType == LeaveType.None && workingHours == DefaultHours(businessDate);
        if (isDefault)
        {
            if (stored.ContainsKey(businessDate) || pending.ContainsKey(businessDate))
                staging.StageCells([new StagedCell { EmployeeId = employeeId, Date = businessDate, Shift = ShiftKey, WorkshopId = employee.CurrentWorkshopId, Revert = true, Actor = Actor }]);
        }
        else
            staging.StageCells([new StagedCell { EmployeeId = employeeId, Date = businessDate, Shift = ShiftKey, WorkshopId = employee.CurrentWorkshopId, Hours = workingHours, LeaveType = (int)leaveType, LeaveHours = leaveType == LeaveType.None ? 0 : leaveHours, Actor = Actor }]);
        return new JsonResult(new { ok = true, weeklyTotal = weeklyHours + workingHours });
    }

    public async Task<IActionResult> OnPostMotherLeaveAsync(int employeeId, DateOnly startDate, DateOnly endDate)
    {
        if (startDate == default || endDate == default || endDate < startDate)
            return new JsonResult(new { ok = false, error = "Select a valid Mother Leave date range." });
        if (endDate.DayNumber - startDate.DayNumber > 3660)
            return new JsonResult(new { ok = false, error = "Select a range of up to 10 years." });
        if (!User.IsInRole(Roles.Admin) && startDate < BusinessDateService.Current())
            return new JsonResult(new { ok = false, error = "Only Admin can edit a previous business date." });

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId && e.EmploymentState == EmploymentState.Active && e.SpecialGroups.Any(g => g.SpecialGroup!.Name == GroupName));
        if (employee is null) return new JsonResult(new { ok = false, error = "Select an active Pregnant employee." });

        var changes = new List<StagedCell>();
        for (var day = startDate; day <= endDate; day = day.AddDays(1))
        {
            if (IsDefaultOffDay(day)) continue;
            changes.Add(new StagedCell { EmployeeId = employeeId, Date = day, Shift = ShiftKey, WorkshopId = employee.CurrentWorkshopId, Hours = 0, LeaveType = (int)LeaveType.MotherLeave, LeaveHours = StandardHours, Actor = Actor });
        }
        if (changes.Count == 0) return new JsonResult(new { ok = false, error = "The selected range contains only OFF days." });
        staging.StageCells(changes);
        return new JsonResult(new { ok = true, count = changes.Count });
    }

    void SetCalendarRange()
    {
        var current = BusinessDateService.Current(); var defaultStart = new DateOnly(current.Year, current.Month, 1); var start = ParseMonth(StartMonth, defaultStart); var end = ParseMonth(EndMonth, start);
        if (end < start) end = start; if (MonthsApart(start, end) > 2) end = start.AddMonths(2);
        StartMonth = start.ToString("yyyy-MM"); EndMonth = end.ToString("yyyy-MM"); var lastDay = end.AddMonths(1).AddDays(-1);
        Days = Enumerable.Range(0, lastDay.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i)).ToList();
        WeekGroups = Days.Select((day, index) => new { day, index })
            .GroupBy(x => x.day.AddDays(-(int)x.day.DayOfWeek))
            .Select(group => new IndexModel.WeekGroup(group.First().day, group.Last().day, group.First().index, group.Count()))
            .ToList();
    }
    static DateOnly ParseMonth(string? value, DateOnly fallback) => DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", out var parsed) ? new DateOnly(parsed.Year, parsed.Month, 1) : fallback;
    static int MonthsApart(DateOnly start, DateOnly end) => (end.Year - start.Year) * 12 + end.Month - start.Month;
    static bool IsDefaultOffDay(DateOnly day) => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
