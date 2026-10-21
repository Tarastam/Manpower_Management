using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Pages.Attendance;

// Works like the By Shift grid (same attendance-grid.js, same Stage handler contract), but for the Pregnant group:
// 8.00 hrs Monday to Friday, Saturday and Sunday OFF. Their hours count as indirect hours in the daily summary.
public class PregnantModel(AppDbContext db, LeaderSessionService leaderSessions, AttendanceStagingStore staging) : PageModel
{
    LeaderSession? leader;
    string Actor => leader?.Actor ?? User.Identity?.Name ?? "Supervisor";

    // Same gate as the By Shift page: a checked-in leader or a signed-in Admin.
    public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context, Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        leader = await leaderSessions.ReadAsync(HttpContext, db);
        if (leader is null && !User.IsInRole(Roles.Admin))
        {
            // The grid autosaves with fetch, which needs a status code rather than a redirect to the check-in page.
            context.Result = Request.Query["handler"] == "Stage"
                ? new JsonResult(new { ok = false, error = "Check in again to keep editing." }) { StatusCode = StatusCodes.Status401Unauthorized }
                : RedirectToPage("CheckIn", new { returnPage = "Pregnant" });
            return;
        }
        await next();
    }

    public const string GroupName = "Pregnant";
    public const string ShiftKey = "Pregnant";
    public const decimal StandardHours = 8m;
    public const decimal MaxWeeklyHours = 48m;

    [BindProperty(SupportsGet = true)] public string Month { get; set; } = "";
    [BindProperty(SupportsGet = true)] public int? WorkshopId { get; set; }
    public DateOnly MonthStart { get; set; }
    public List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem> WorkshopOptions { get; set; } = [];
    // Every workshop with active employees, for "worked in" on a transfer.
    public List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem> TransferOptions { get; set; } = [];
    // Employee id → the workshop they belong to; a cell counted in any other workshop is a transfer.
    public Dictionary<int, int> HomeWorkshops { get; set; } = [];
    public List<IndexModel.GridRow> GridRows { get; set; } = [];
    public List<DateOnly> Days { get; set; } = [];
    public bool GroupMissing { get; set; }

    IQueryable<Employee> PregnantQuery() => db.Employees.Where(e => e.EmploymentState == EmploymentState.Active && e.SpecialGroups.Any(g => g.SpecialGroup!.Name == GroupName));

    public async Task OnGetAsync()
    {
        SetCalendarRange();
        var employees = await PregnantQuery().OrderBy(e => e.FullName).ToListAsync();
        if (!await db.SpecialGroups.AnyAsync(g => g.Name == GroupName)) { GroupMissing = true; }
        var workshopIds = employees.Select(e => e.CurrentWorkshopId).Distinct().ToList();
        WorkshopOptions = (await db.Workshops.Where(w => workshopIds.Contains(w.Id)).OrderBy(w => w.Name).ToListAsync())
            .Select(w => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(w.Name, w.Id.ToString())).ToList();
        if (WorkshopId is int workshopId) employees = employees.Where(e => e.CurrentWorkshopId == workshopId).ToList();
        TransferOptions = await db.Employees.Where(e => e.EmploymentState == EmploymentState.Active).Select(e => e.CurrentWorkshop!).Where(w => w != null).Distinct().OrderBy(w => w.Name)
            .Select(w => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(w.Name, w.Id.ToString())).ToListAsync();
        var employeeIds = employees.Select(e => e.Id).ToHashSet();
        HomeWorkshops = employees.ToDictionary(e => e.Id, e => e.CurrentWorkshopId);
        var first = Days.First(); var last = Days.Last();
        var entries = await db.AttendanceEntries
            .Where(e => e.Shift == ShiftKey && e.IsConfirmed && e.BusinessDate >= first && e.BusinessDate <= last && employeeIds.Contains(e.EmployeeId))
            .ToDictionaryAsync(e => (e.EmployeeId, e.BusinessDate));
        // Changes still waiting for the 07:00 / 19:00 copy to SQL cover what is stored.
        var staged = staging.PendingCells(c => c.Shift == ShiftKey && c.Date >= first && c.Date <= last && employeeIds.Contains(c.EmployeeId)).ToDictionary(c => (c.EmployeeId, c.Date));
        GridRows = employees.Select(employee => new IndexModel.GridRow
        {
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeId,
            EmployeeName = employee.FullName,
            EmployeeShift = employee.Shift,
            Process = employee.Process,
            Cells = Days.Select(day =>
            {
                var isOffDay = IsDefaultOffDay(day);
                var standard = new IndexModel.AttendanceCell { BusinessDate = day, WorkshopId = employee.CurrentWorkshopId, WorkingHours = isOffDay ? 0 : StandardHours, IsOffDay = isOffDay, IsConfirmed = true };
                if (staged.TryGetValue((employee.Id, day), out var change))
                    return change.Revert ? standard : new IndexModel.AttendanceCell { BusinessDate = day, WorkingHours = change.Hours, WorkshopId = change.WorkshopId, LeaveType = isOffDay ? LeaveType.None : (LeaveType)change.LeaveType, LeaveHours = isOffDay ? 0 : change.LeaveHours, IsOffDay = isOffDay, IsConfirmed = true };
                if (entries.TryGetValue((employee.Id, day), out var entry))
                    return new IndexModel.AttendanceCell { BusinessDate = day, WorkingHours = entry.WorkingHours, WorkshopId = entry.WorkshopId, LeaveType = isOffDay ? LeaveType.None : entry.LeaveType, LeaveHours = isOffDay ? 0 : entry.LeaveHours, IsOffDay = isOffDay, IsConfirmed = true };
                return standard;
            }).ToList()
        }).ToList();

        // Remarks: stored ones, covered by any change still waiting in the staging file (an empty text clears it).
        var remarks = (await db.AttendanceNotes.AsNoTracking().Where(n => n.BusinessDate >= first && n.BusinessDate <= last && employeeIds.Contains(n.EmployeeId)).ToListAsync())
            .ToDictionary(n => (n.EmployeeId, n.BusinessDate), n => n.Text);
        foreach (var n in staging.PendingNotes(x => x.Date >= first && x.Date <= last && employeeIds.Contains(x.EmployeeId))) remarks[(n.EmployeeId, n.Date)] = n.Text;
        foreach (var row in GridRows)
            foreach (var cell in row.Cells)
                if (remarks.TryGetValue((row.EmployeeId, cell.BusinessDate), out var text)) cell.Note = text;
    }

    // Called by the grid as people type: the change is kept in the staging file and reaches SQL at 07:00 / 19:00.
    public async Task<IActionResult> OnPostStageAsync([FromBody] IndexModel.StageRequest request)
    {
        SetCalendarRange();
        var today = BusinessDateService.Current();
        var failures = new List<IndexModel.StageFailure>();
        var cells = (request.Cells ?? []).GroupBy(c => (c.EmployeeId, c.Date)).Select(g => g.Last()).ToList();
        var roster = await PregnantQuery().Select(e => new { e.Id, e.FullName, e.CurrentWorkshopId }).ToDictionaryAsync(e => e.Id);
        var validWorkshops = (await db.Workshops.Select(w => w.Id).ToListAsync()).ToHashSet();
        var daySet = Days.ToHashSet();
        var first = Days.First(); var last = Days.Last();
        var employeeIds = cells.Select(c => c.EmployeeId).Where(roster.ContainsKey).Distinct().ToList();
        var stored = employeeIds.Count == 0 ? new Dictionary<(int, DateOnly), AttendanceEntry>() : (await db.AttendanceEntries
            .Where(e => e.Shift == ShiftKey && e.IsConfirmed && e.BusinessDate >= first && e.BusinessDate <= last && employeeIds.Contains(e.EmployeeId)).ToListAsync())
            .ToDictionary(e => (e.EmployeeId, e.BusinessDate));
        var pending = staging.PendingCells(c => c.Shift == ShiftKey && c.Date >= first && c.Date <= last && employeeIds.Contains(c.EmployeeId)).ToDictionary(c => (c.EmployeeId, c.Date));
        static decimal DefaultHours(DateOnly day) => IsDefaultOffDay(day) ? 0 : StandardHours;

        var accepted = new List<IndexModel.StageCellIn>();
        foreach (var cell in cells)
        {
            if (!roster.ContainsKey(cell.EmployeeId) || !daySet.Contains(cell.Date)) continue;
            var isOff = IsDefaultOffDay(cell.Date);
            string? problem = null;
            if (!Enum.IsDefined(typeof(LeaveType), cell.LeaveType)) problem = "Unknown leave type.";
            else if (cell.WorkshopId != 0 && !validWorkshops.Contains(cell.WorkshopId)) problem = "Unknown workshop.";
            else if (isOff && cell.LeaveType != 0) problem = $"{cell.Date:dd MMM yyyy} is an OFF day. Only OT working hours can be entered.";
            else if (cell.LeaveType != 0 && cell.LeaveHours <= 0) problem = "Leave hours must be more than 0.";
            else if (cell.Hours < 0 || cell.LeaveHours < 0 || cell.Hours + cell.LeaveHours > StandardHours) problem = $"Hours must total at most {StandardHours}.";
            else if (cell.Date > today && cell.LeaveType == 0 && cell.Hours != DefaultHours(cell.Date)) problem = "Only leave can be entered ahead of today.";
            if (problem is not null) { failures.Add(new IndexModel.StageFailure(cell.EmployeeId, cell.Date, problem)); continue; }
            accepted.Add(cell);
        }

        // The weekly total counts the default hours too, so use what each day holds after this request (keyed, pending, stored or default).
        var incoming = accepted.ToDictionary(c => (c.EmployeeId, c.Date));
        decimal HoursFor(int employeeId, DateOnly day) =>
            incoming.TryGetValue((employeeId, day), out var c) ? c.Hours
            : pending.TryGetValue((employeeId, day), out var p) ? (p.Revert ? DefaultHours(day) : p.Hours)
            : stored.TryGetValue((employeeId, day), out var e) ? e.WorkingHours : DefaultHours(day);
        foreach (var employeeId in accepted.Select(c => c.EmployeeId).Distinct().ToList())
            foreach (var week in Days.Select((day, i) => (day, i)).GroupBy(x => x.i / 7))
            {
                var weekDays = week.Select(x => x.day).ToList();
                if (weekDays.Sum(day => HoursFor(employeeId, day)) <= MaxWeeklyHours) continue;
                foreach (var cell in accepted.Where(c => c.EmployeeId == employeeId && c.Date >= weekDays[0] && c.Date <= weekDays[^1]).ToList())
                {
                    failures.Add(new IndexModel.StageFailure(cell.EmployeeId, cell.Date, $"Weekly working hours for {roster[employeeId].FullName} ({weekDays[0]:dd MMM} - {weekDays[^1]:dd MMM yyyy}) cannot exceed {MaxWeeklyHours:0} hours."));
                    accepted.Remove(cell);
                }
            }

        var changes = new List<StagedCell>();
        foreach (var cell in accepted)
        {
            var home = roster[cell.EmployeeId].CurrentWorkshopId;
            var workshop = cell.WorkshopId == 0 ? home : cell.WorkshopId;
            // Anything that equals the default (and is not a transfer) is not stored: it reverts the day.
            var isDefault = cell.LeaveType == 0 && cell.Hours == DefaultHours(cell.Date) && workshop == home;
            stored.TryGetValue((cell.EmployeeId, cell.Date), out var entry);
            var hasPending = pending.ContainsKey((cell.EmployeeId, cell.Date));
            if (isDefault)
            {
                if (entry is null && !hasPending) continue;
                changes.Add(new StagedCell { EmployeeId = cell.EmployeeId, Date = cell.Date, Shift = ShiftKey, WorkshopId = home, Revert = true, Actor = Actor });
                continue;
            }
            var leaveHours = cell.LeaveType == 0 ? 0 : cell.LeaveHours;
            if (!hasPending && entry is not null && entry.WorkshopId == workshop && entry.WorkingHours == cell.Hours && entry.LeaveType == (LeaveType)cell.LeaveType && entry.LeaveHours == leaveHours) continue;
            changes.Add(new StagedCell { EmployeeId = cell.EmployeeId, Date = cell.Date, Shift = ShiftKey, WorkshopId = workshop, Hours = cell.Hours, LeaveType = cell.LeaveType, LeaveHours = leaveHours, Actor = Actor });
        }

        // Remarks are kept apart from the hours, so a remark alone never creates an attendance entry.
        var remarks = new List<StagedNote>();
        var noteIds = (request.Notes ?? []).Select(n => n.EmployeeId).Where(roster.ContainsKey).Distinct().ToList();
        var storedNotes = noteIds.Count == 0 ? new Dictionary<(int, DateOnly), string>() : (await db.AttendanceNotes
            .Where(n => n.BusinessDate >= first && n.BusinessDate <= last && noteIds.Contains(n.EmployeeId)).ToListAsync())
            .ToDictionary(n => (n.EmployeeId, n.BusinessDate), n => n.Text);
        var pendingNotes = staging.PendingNotes(n => n.Date >= first && n.Date <= last && noteIds.Contains(n.EmployeeId)).ToDictionary(n => (n.EmployeeId, n.Date), n => n.Text);
        foreach (var note in (request.Notes ?? []).GroupBy(n => (n.EmployeeId, n.Date)).Select(g => g.Last()))
        {
            if (!roster.ContainsKey(note.EmployeeId) || !daySet.Contains(note.Date)) continue;
            var text = (note.Text ?? "").Trim();
            if (text.Length > AttendanceNote.MaxLength) { failures.Add(new IndexModel.StageFailure(note.EmployeeId, note.Date, $"A remark can be at most {AttendanceNote.MaxLength} characters.")); continue; }
            var current = pendingNotes.TryGetValue((note.EmployeeId, note.Date), out var p) ? p : storedNotes.GetValueOrDefault((note.EmployeeId, note.Date)) ?? "";
            if (current == text) continue;
            remarks.Add(new StagedNote { EmployeeId = note.EmployeeId, Date = note.Date, Text = text, Actor = Actor });
        }

        if (changes.Count > 0) staging.StageCells(changes);
        if (remarks.Count > 0) staging.StageNotes(remarks);
        return new JsonResult(new { ok = true, saved = changes.Count + remarks.Count, failures });
    }

    // Mother Leave for every Monday–Friday of a date range, which may reach outside the displayed month.
    public async Task<IActionResult> OnPostMotherLeaveAsync(int employeeId, DateOnly startDate, DateOnly endDate)
    {
        if (startDate == default || endDate == default || endDate < startDate)
            return new JsonResult(new { ok = false, error = "Select a valid Mother Leave date range." });
        if (endDate.DayNumber - startDate.DayNumber > 3660)
            return new JsonResult(new { ok = false, error = "Select a range of up to 10 years." });
        var employee = await PregnantQuery().FirstOrDefaultAsync(e => e.Id == employeeId);
        if (employee is null) return new JsonResult(new { ok = false, error = "Select an active Pregnant employee." });

        var changes = new List<StagedCell>();
        for (var day = startDate; day <= endDate; day = day.AddDays(1))
            if (!IsDefaultOffDay(day))
                changes.Add(new StagedCell { EmployeeId = employeeId, Date = day, Shift = ShiftKey, WorkshopId = employee.CurrentWorkshopId, Hours = 0, LeaveType = (int)LeaveType.MotherLeave, LeaveHours = StandardHours, Actor = Actor });
        if (changes.Count == 0) return new JsonResult(new { ok = false, error = "The selected range contains only OFF days." });
        staging.StageCells(changes);
        return new JsonResult(new { ok = true, count = changes.Count });
    }

    // Whole Sunday–Saturday weeks around the month, like the By Shift grid, so weekly totals are complete.
    void SetCalendarRange()
    {
        var current = BusinessDateService.Current();
        MonthStart = DateOnly.TryParseExact(Month + "-01", "yyyy-MM-dd", out var parsed) ? new DateOnly(parsed.Year, parsed.Month, 1) : new DateOnly(current.Year, current.Month, 1);
        Month = MonthStart.ToString("yyyy-MM");
        var monthEnd = MonthStart.AddMonths(1).AddDays(-1);
        var start = MonthStart.AddDays(-(int)MonthStart.DayOfWeek);
        var end = monthEnd.AddDays(6 - (int)monthEnd.DayOfWeek);
        Days = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i)).ToList();
    }
    static bool IsDefaultOffDay(DateOnly day) => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
