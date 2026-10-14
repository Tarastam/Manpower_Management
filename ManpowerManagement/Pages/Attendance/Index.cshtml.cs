using System.Text;
using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ManpowerManagement.Pages.Attendance;

public class IndexModel(AppDbContext db, IMemoryCache cache, LeaderSessionService leaderSessions, DailyHoursService dailyHours, AttendanceStagingStore staging) : PageModel
{
    const string WorkshopOptionsCacheKey = "attendance:workshop-options";
    public const decimal StandardHours = 10.67m;
    public const decimal MaxWeeklyHours = 60m;
    // The September 2026 shift calendar uses one 4-work / 2-OFF cycle with staggered starts.
    // Imported calendar data takes precedence over these reference dates.
    internal static readonly IReadOnlyDictionary<string, DateOnly> ShiftReferenceWorkingDays = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = new(2026, 9, 3),
        ["B"] = new(2026, 9, 5),
        ["C"] = new(2026, 9, 1)
    };
    // The same short codes supervisors already type in the Excel attendance sheets.
    public static readonly IReadOnlyDictionary<LeaveType, string> LeaveCodes = new Dictionary<LeaveType, string>
    {
        [LeaveType.AnnualLeave] = "AL",
        [LeaveType.SickLeave] = "SL",
        [LeaveType.BusinessLeave] = "BL",
        [LeaveType.OtherLeave] = "OL",
        [LeaveType.Absent] = "AB",
        [LeaveType.MotherLeave] = "ML"
    };
    public class AttendanceCell { public DateOnly BusinessDate { get; set; } public decimal WorkingHours { get; set; } = StandardHours; public int WorkshopId { get; set; } public LeaveType LeaveType { get; set; } = LeaveType.None; public decimal LeaveHours { get; set; } public bool IsOffDay { get; set; } public bool IsConfirmed { get; set; } public bool IsResigned { get; set; } public string Note { get; set; } = ""; }
    public class GridRow { public int EmployeeId { get; set; } public string EmployeeCode { get; set; } = ""; public string EmployeeName { get; set; } = ""; public string EmployeeShift { get; set; } = ""; public string Process { get; set; } = ""; public bool IsLeader { get; set; } public List<AttendanceCell> Cells { get; set; } = []; }
    public record WeekGroup(DateOnly Start, DateOnly End, int StartIndex, int Count);
    // Keyed is false for a day whose summary row was created automatically and nobody entered the indirect headcount yet.
    public record ManpowerDay(int? DirectCount, int? IndirectCount, int I1067, int I841, int I733,
        decimal? TotalHours, decimal? IndirectHours, bool IndirectHoursIsManual, bool Keyed = true)
    {
        public decimal? DirectHours => TotalHours - IndirectHours;
    }
    // The browser sends only the cells and headcounts that changed; they are kept in the staging file until 07:00 / 19:00.
    public record StageCellIn(int EmployeeId, DateOnly Date, decimal Hours, int LeaveType, decimal LeaveHours, int WorkshopId);
    public record StageManpowerIn(DateOnly Date, int I1067, int I841, int I733);
    // An empty text clears the remark.
    public record StageNoteIn(int EmployeeId, DateOnly Date, string? Text);
    public record StageRequest(List<StageCellIn>? Cells, List<StageManpowerIn>? Manpower, List<StageNoteIn>? Notes = null);
    public record StageFailure(int EmployeeId, DateOnly Date, string Message);

    // Admin only: a checked-in leader's workshop and shift always come from the leader session.
    [BindProperty(SupportsGet = true)] public int? WorkshopId { get; set; }
    [BindProperty(SupportsGet = true)] public string Shift { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Month { get; set; } = "";
    public LeaderSession? Leader { get; set; }
    public string WorkshopName { get; set; } = "";
    public DateOnly MonthStart { get; set; }
    public List<SelectListItem> WorkshopOptions { get; set; } = [];
    public List<SelectListItem> ShiftOptions { get; set; } = [];
    public List<GridRow> GridRows { get; set; } = [];
    public List<DateOnly> Days { get; set; } = [];
    public List<WeekGroup> WeekGroups { get; set; } = [];
    public HashSet<DateOnly> OffDays { get; set; } = [];
    public Dictionary<DateOnly, ManpowerDay> Manpower { get; set; } = [];
    public string? Error { get; set; }
    [TempData] public string? Message { get; set; }
    public bool IsAdmin => User.IsInRole(Roles.Admin);
    // A Day-shift leader supervises the whole workshop, so they can view, key and edit every shift in it.
    // Anyone on the Attendance Leader list is always indirect: counted for attendance only, never for hours.
    public static Task<List<int>> LeaderEmployeeIdsAsync(AppDbContext db) =>
        db.Employees.Where(e => db.AttendanceLeaders.Any(l => l.IsActive && l.EmployeeCode == e.EmployeeId)).Select(e => e.Id).ToListAsync();

    // Leaders are assigned per workshop, so they can key every shift in it.
    public bool CanSwitchShift => Leader is not null;
    public List<string> AllowedShifts { get; set; } = [];
    // Workshops under the leader's line that have active employees.
    public List<SelectListItem> AllowedWorkshops { get; set; } = [];
    public bool HasScope => WorkshopId is not null && !string.IsNullOrEmpty(Shift);
    string Actor => Leader?.Actor ?? User.Identity?.Name ?? "Supervisor";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await ResolveScopeAsync()) return RedirectToPage("CheckIn");
        await Options(); SetCalendarRange();
        if (HasScope) await LoadGridAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetExportCsvAsync()
    {
        if (!await ResolveScopeAsync()) return RedirectToPage("CheckIn");
        await Options(); SetCalendarRange();
        if (!HasScope) { Error = "Select a workshop and shift."; return Page(); }
        await LoadGridAsync();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', new[] { "Emp ID", "Employee", "Process", "Shift" }.Concat(Days.Select(d => d.ToString("yyyy-MM-dd"))).Append("Remarks").Select(CsvField)));
        foreach (var row in GridRows)
            sb.AppendLine(string.Join(',', new[] { row.EmployeeCode, row.EmployeeName, row.Process, row.EmployeeShift }.Concat(row.Cells.Select(c => c.IsResigned ? "Resigned" : CellCode(c)))
                .Append(string.Join("; ", row.Cells.Where(c => c.Note != "").Select(c => $"{c.BusinessDate:dd/MM}: {c.Note}"))).Select(CsvField)));
        sb.AppendLine(string.Join(',', new[] { "", "Direct MP", "", "" }.Concat(Days.Select(d => Manpower.TryGetValue(d, out var m) ? m.DirectCount?.ToString() ?? "" : "")).Select(CsvField)));
        sb.AppendLine(string.Join(',', new[] { "", "Indirect MP", "", "" }.Concat(Days.Select(d => Manpower.TryGetValue(d, out var m) ? m.IndirectCount?.ToString() ?? "" : "")).Select(CsvField)));
        var fileName = $"attendance_{WorkshopName}_{Shift}_{MonthStart:yyyy-MM}.csv";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", fileName);
    }

    // The value a supervisor would type in the grid: 10.67, OFF, AL, SL4 ...
    public static string CellCode(AttendanceCell cell)
    {
        if (cell.LeaveType != LeaveType.None)
        {
            var code = LeaveCodes.TryGetValue(cell.LeaveType, out var c) ? c : "OL";
            return cell.LeaveHours >= StandardHours ? code : $"{code}{cell.LeaveHours:0.##}";
        }
        if (cell.IsOffDay && cell.WorkingHours == 0) return "OFF";
        return cell.WorkingHours.ToString("0.00");
    }

    static string CsvField(string? value)
    {
        value ??= "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    // Called by the grid as people type: the change is kept in the staging file and reaches SQL at 07:00 / 19:00.
    public async Task<IActionResult> OnPostStageAsync([FromBody] StageRequest request)
    {
        if (!await ResolveScopeAsync()) return new JsonResult(new { ok = false, error = "Check in again to keep editing." }) { StatusCode = StatusCodes.Status401Unauthorized };
        SetCalendarRange();
        if (WorkshopId is not int workshopId || string.IsNullOrEmpty(Shift)) return new JsonResult(new { ok = false, error = "Select a workshop and shift." });
        var today = BusinessDateService.Current();
        var failures = new List<StageFailure>();
        var cells = (request.Cells ?? []).GroupBy(c => (c.EmployeeId, c.Date)).Select(g => g.Last()).ToList();

        var rosterRows = await RosterQuery(workshopId).Select(e => new { e.Id, e.FullName, e.ResignedDate }).ToListAsync();
        var roster = rosterRows.ToDictionary(e => e.Id);
        var leaderIds = (await LeaderEmployeeIdsAsync(db)).ToHashSet();
        var validWorkshops = (await db.Workshops.Select(w => w.Id).ToListAsync()).ToHashSet();
        var daySet = Days.ToHashSet();
        var employeeIds = cells.Select(c => c.EmployeeId).Where(roster.ContainsKey).Distinct().ToList();
        var first = Days.First(); var last = Days.Last(); var shift = Shift;
        var stored = employeeIds.Count == 0 ? new Dictionary<(int, DateOnly), AttendanceEntry>() : (await db.AttendanceEntries
            .Where(e => e.Shift == shift && e.IsConfirmed && e.BusinessDate >= first && e.BusinessDate <= last && employeeIds.Contains(e.EmployeeId)).ToListAsync())
            .ToDictionary(e => (e.EmployeeId, e.BusinessDate));
        var pending = staging.PendingCells(c => c.Shift == shift && c.Date >= first && c.Date <= last && employeeIds.Contains(c.EmployeeId)).ToDictionary(c => (c.EmployeeId, c.Date));

        // What each affected employee would have in the grid after this request, for the weekly limit.
        decimal HoursFor(int employeeId, DateOnly day, Dictionary<(int, DateOnly), StageCellIn> incoming)
        {
            if (roster[employeeId].ResignedDate is DateOnly lastDay && day > lastDay) return 0;
            if (incoming.TryGetValue((employeeId, day), out var c)) return c.Hours;
            if (pending.TryGetValue((employeeId, day), out var p)) return p.Revert ? (OffDays.Contains(day) ? 0 : StandardHours) : p.Hours;
            if (stored.TryGetValue((employeeId, day), out var e)) return e.WorkingHours;
            return OffDays.Contains(day) ? 0 : StandardHours;
        }

        var accepted = new List<StageCellIn>();
        foreach (var cell in cells)
        {
            if (!roster.TryGetValue(cell.EmployeeId, out var person) || !daySet.Contains(cell.Date)) continue;
            // Nothing is recorded after an employee's last working day, whatever the browser sends.
            if (person.ResignedDate is DateOnly lastDay && cell.Date > lastDay) continue;
            var isOff = OffDays.Contains(cell.Date);
            string? problem = null;
            if (!Enum.IsDefined(typeof(LeaveType), cell.LeaveType)) problem = "Unknown leave type.";
            else if (cell.WorkshopId != 0 && !validWorkshops.Contains(cell.WorkshopId)) problem = "Unknown workshop.";
            else if (isOff && cell.LeaveType != 0) problem = $"{cell.Date:dd MMM yyyy} is an OFF day. Only OT working hours can be entered.";
            else if (cell.LeaveType != 0 && cell.LeaveHours <= 0) problem = "Leave hours must be more than 0.";
            else if (cell.Hours < 0 || cell.LeaveHours < 0 || cell.Hours + cell.LeaveHours > StandardHours) problem = $"Hours must total at most {StandardHours}.";
            else if (cell.Date > today && cell.LeaveType == 0 && cell.Hours != (isOff ? 0 : StandardHours)) problem = "Only leave can be entered ahead of today.";
            if (problem is not null) { failures.Add(new StageFailure(cell.EmployeeId, cell.Date, problem)); continue; }
            accepted.Add(cell);
        }

        // The weekly limit does not apply to attendance leaders, who are counted for attendance only.
        var incoming = accepted.ToDictionary(c => (c.EmployeeId, c.Date));
        foreach (var employeeId in accepted.Select(c => c.EmployeeId).Distinct().Where(id => !leaderIds.Contains(id)).ToList())
            foreach (var week in WeekGroups)
            {
                var total = Days.Skip(week.StartIndex).Take(week.Count).Sum(day => HoursFor(employeeId, day, incoming));
                if (total <= MaxWeeklyHours) continue;
                foreach (var cell in accepted.Where(c => c.EmployeeId == employeeId && c.Date >= week.Start && c.Date <= week.End).ToList())
                {
                    failures.Add(new StageFailure(cell.EmployeeId, cell.Date, $"Weekly working hours for {roster[employeeId].FullName} ({week.Start:dd MMM} - {week.End:dd MMM yyyy}) cannot exceed {MaxWeeklyHours:0} hours."));
                    accepted.Remove(cell);
                }
            }

        var changes = new List<StagedCell>();
        foreach (var cell in accepted)
        {
            var isOff = OffDays.Contains(cell.Date);
            var workshop = cell.WorkshopId == 0 ? workshopId : cell.WorkshopId;
            // Future dates only keep leave; anything else there means "no leave", which is the default.
            var isDefault = cell.LeaveType == 0 && cell.Hours == (isOff ? 0 : StandardHours) && workshop == workshopId;
            stored.TryGetValue((cell.EmployeeId, cell.Date), out var entry);
            var hasPending = pending.ContainsKey((cell.EmployeeId, cell.Date));
            if (isDefault)
            {
                if (entry is null && !hasPending) continue;
                changes.Add(new StagedCell { EmployeeId = cell.EmployeeId, Date = cell.Date, Shift = Shift, WorkshopId = workshopId, Revert = true, Actor = Actor });
                continue;
            }
            var leaveHours = cell.LeaveType == 0 ? 0 : cell.LeaveHours;
            if (!hasPending && entry is not null && entry.WorkshopId == workshop && entry.WorkingHours == cell.Hours && entry.LeaveType == (LeaveType)cell.LeaveType && entry.LeaveHours == leaveHours) continue;
            changes.Add(new StagedCell { EmployeeId = cell.EmployeeId, Date = cell.Date, Shift = Shift, WorkshopId = workshop, Hours = cell.Hours, LeaveType = cell.LeaveType, LeaveHours = leaveHours, Actor = Actor });
        }

        var manpower = new List<StagedManpower>();
        foreach (var m in request.Manpower ?? [])
        {
            if (!daySet.Contains(m.Date)) continue;
            if (m.Date > today || m.I1067 < 0 || m.I841 < 0 || m.I733 < 0) { failures.Add(new StageFailure(0, m.Date, $"Indirect manpower for {m.Date:dd MMM yyyy} cannot be entered ahead of today or be negative.")); continue; }
            manpower.Add(new StagedManpower { WorkshopId = workshopId, Shift = Shift, Date = m.Date, I1067 = m.I1067, I841 = m.I841, I733 = m.I733, Actor = Actor });
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
            if (text.Length > AttendanceNote.MaxLength) { failures.Add(new StageFailure(note.EmployeeId, note.Date, $"A remark can be at most {AttendanceNote.MaxLength} characters.")); continue; }
            var current = pendingNotes.TryGetValue((note.EmployeeId, note.Date), out var p) ? p : storedNotes.GetValueOrDefault((note.EmployeeId, note.Date)) ?? "";
            if (current == text) continue;
            remarks.Add(new StagedNote { EmployeeId = note.EmployeeId, Date = note.Date, Text = text, Actor = Actor });
        }

        if (changes.Count > 0) staging.StageCells(changes);
        if (manpower.Count > 0) staging.StageManpower(manpower);
        if (remarks.Count > 0) staging.StageNotes(remarks);
        return new JsonResult(new { ok = true, saved = changes.Count + manpower.Count + remarks.Count, failures });
    }
    public async Task<IActionResult> OnPostIndirectHoursAsync(DateOnly businessDate, decimal indirectHours)
    {
        if (!IsAdmin) return Forbid();
        if (!await ResolveScopeAsync()) return RedirectToPage("CheckIn");
        if (WorkshopId is not int workshopId || string.IsNullOrEmpty(Shift)) return BadRequest();
        if (businessDate >= BusinessDateService.Current() || indirectHours < 0) return BadRequest();
        var summary = await db.ShiftManpowers.SingleOrDefaultAsync(m =>
            m.BusinessDate == businessDate && m.WorkshopId == workshopId && m.Shift == Shift);
        if (summary is null) return NotFound();
        await dailyHours.RefreshAsync(businessDate, businessDate);
        if (indirectHours > summary.TotalHours) return BadRequest("Indirect hours cannot exceed total hours.");
        summary.IndirectHours = indirectHours;
        summary.IndirectHoursIsManual = true;
        summary.CalculatedAtUtc = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog { EntityName = "ShiftManpower", EntityKey = summary.Id.ToString(),
            Action = "Corrected indirect hours", Actor = Actor, Detail = $"{businessDate:yyyy-MM-dd}: {indirectHours:0.00} hours" });
        await db.SaveChangesAsync();
        Message = $"Indirect hours updated for {businessDate:dd MMM yyyy}.";
        return RedirectToPage(new { workshopId, shift = Shift, month = businessDate.ToString("yyyy-MM") });
    }

    public async Task<IActionResult> OnPostResetIndirectHoursAsync(DateOnly businessDate)
    {
        if (!IsAdmin) return Forbid();
        if (!await ResolveScopeAsync()) return RedirectToPage("CheckIn");
        if (WorkshopId is not int workshopId || string.IsNullOrEmpty(Shift)) return BadRequest();
        if (businessDate >= BusinessDateService.Current()) return BadRequest();
        var summary = await db.ShiftManpowers.SingleOrDefaultAsync(m =>
            m.BusinessDate == businessDate && m.WorkshopId == workshopId && m.Shift == Shift);
        if (summary is null) return NotFound();
        summary.IndirectHoursIsManual = false;
        await dailyHours.RefreshAsync(businessDate, businessDate);
        db.AuditLogs.Add(new AuditLog { EntityName = "ShiftManpower", EntityKey = summary.Id.ToString(),
            Action = "Reset indirect hours", Actor = Actor, Detail = businessDate.ToString("yyyy-MM-dd") });
        await db.SaveChangesAsync();
        Message = $"Indirect hours reset for {businessDate:dd MMM yyyy}.";
        return RedirectToPage(new { workshopId, shift = Shift, month = businessDate.ToString("yyyy-MM") });
    }

    async Task<bool> ResolveScopeAsync()
    {
        Leader = await leaderSessions.ReadAsync(HttpContext, db);
        if (Leader is not null)
        {
            // Leaders are assigned to a line; the requested workshop and shift are only honoured inside it.
            var workshops = await db.Workshops.Select(w => new { w.Id, w.Name, w.ParentId }).ToListAsync();
            var inLine = new HashSet<int> { Leader.WorkshopId };
            for (var added = true; added;) added = workshops.Where(w => w.ParentId is int p && inLine.Contains(p)).Aggregate(false, (any, w) => inLine.Add(w.Id) | any);
            var staffed = await db.Employees.Where(e => e.EmploymentState == EmploymentState.Active && inLine.Contains(e.CurrentWorkshopId)).Select(e => e.CurrentWorkshopId).Distinct().ToListAsync();
            AllowedWorkshops = workshops.Where(w => staffed.Contains(w.Id)).OrderBy(w => w.Name).Select(w => new SelectListItem(w.Name, w.Id.ToString())).ToList();
            if (AllowedWorkshops.Count == 0) AllowedWorkshops.Add(new SelectListItem(Leader.WorkshopName, Leader.WorkshopId.ToString()));
            // Right after check-in nothing is picked yet, so start on the leader's own workshop and shift from the employee master.
            if (WorkshopId is null && string.IsNullOrEmpty(Shift) && Leader.EmployeeKey != 0)
            {
                var own = await db.Employees.Where(e => e.Id == Leader.EmployeeKey).Select(e => new { e.CurrentWorkshopId, e.Shift }).FirstOrDefaultAsync();
                if (own is not null) { WorkshopId = own.CurrentWorkshopId; Shift = own.Shift; }
            }
            WorkshopId = int.Parse((AllowedWorkshops.FirstOrDefault(o => o.Value == WorkshopId?.ToString()) ?? AllowedWorkshops[0]).Value);
            var workshopId = WorkshopId.Value;
            AllowedShifts = await db.Employees.Where(e => e.EmploymentState == EmploymentState.Active && e.CurrentWorkshopId == workshopId).Select(e => e.Shift).Distinct().OrderBy(s => s).ToListAsync();
            if (AllowedShifts.Count == 0) AllowedShifts.Add(LeaderSessionService.DayShift);
            // A leader who is not on the Day shift cannot key the Day shift; leaders outside the employee master keep every shift.
            if (Leader.EmployeeKey != 0)
            {
                var ownShift = await db.Employees.Where(e => e.Id == Leader.EmployeeKey).Select(e => e.Shift).FirstOrDefaultAsync();
                var isDayLeader = string.Equals(ownShift, LeaderSessionService.DayShift, StringComparison.OrdinalIgnoreCase);
                var withoutDay = AllowedShifts.Where(s => !s.Equals(LeaderSessionService.DayShift, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!isDayLeader && withoutDay.Count > 0) AllowedShifts = withoutDay;
            }
            Shift = AllowedShifts.FirstOrDefault(s => s.Equals(Shift, StringComparison.OrdinalIgnoreCase)) ?? AllowedShifts[0];
            return true;
        }
        return IsAdmin;
    }

    // Pregnant employees are recorded on the Pregnant page only, so they must not also appear by shift.
    // An employee with a last working day stays listed (as Resigned) through the end of that month, so they are on the
    // roster for every month up to and including the month of the last working day; Resigned with no date is not listed.
    IQueryable<Employee> RosterQuery(int workshopId) => db.Employees.Where(e => (e.ResignedDate == null ? e.EmploymentState == EmploymentState.Active : e.ResignedDate >= MonthStart) && e.CurrentWorkshopId == workshopId && e.Shift == Shift && !e.SpecialGroups.Any(g => g.SpecialGroup!.Name == PregnantModel.GroupName));

    // What the grid shows: the stored entry, covered by any change still waiting in the staging file, else the default.
    // Everyone is prefilled with the standard hours on a working day, so only exceptions are ever stored.
    async Task LoadGridAsync()
    {
        var workshopId = WorkshopId!.Value;
        var employees = await RosterQuery(workshopId).OrderBy(e => e.FullName).ToListAsync();
        var employeeIds = employees.Select(e => e.Id).ToHashSet();
        var leaderIds = (await LeaderEmployeeIdsAsync(db)).ToHashSet();
        var first = Days.First(); var last = Days.Last(); var shift = Shift;
        var entries = await db.AttendanceEntries.Where(e => e.Shift == shift && e.IsConfirmed && e.BusinessDate >= first && e.BusinessDate <= last && employeeIds.Contains(e.EmployeeId)).ToDictionaryAsync(e => (e.EmployeeId, e.BusinessDate));
        var staged = staging.PendingCells(c => c.Shift == shift && c.Date >= first && c.Date <= last && employeeIds.Contains(c.EmployeeId)).ToDictionary(c => (c.EmployeeId, c.Date));
        Manpower = (await db.ShiftManpowers.Where(m => m.WorkshopId == workshopId && m.Shift == shift && m.BusinessDate >= first && m.BusinessDate <= last).ToListAsync())
            .ToDictionary(m => m.BusinessDate, m => new ManpowerDay(m.DirectCount, m.IndirectCount, m.Indirect1067Count, m.Indirect841Count, m.Indirect733Count,
                m.TotalHours, m.IndirectHours, m.IndirectHoursIsManual, Keyed: m.EnteredBy != ""));
        foreach (var m in staging.PendingManpower(m => m.WorkshopId == workshopId && m.Shift == shift && m.Date >= first && m.Date <= last))
        {
            Manpower.TryGetValue(m.Date, out var stored);
            Manpower[m.Date] = new ManpowerDay(null, null, m.I1067, m.I841, m.I733, stored?.TotalHours, stored?.IndirectHours, stored?.IndirectHoursIsManual ?? false);
        }
        GridRows = employees.Select(employee => new GridRow
        {
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeId,
            EmployeeName = employee.FullName,
            EmployeeShift = employee.Shift,
            Process = employee.Process,
            IsLeader = leaderIds.Contains(employee.Id),
            Cells = Days.Select(day =>
            {
                var isOffDay = OffDays.Contains(day);
                if (employee.ResignedDate is DateOnly lastDay && day > lastDay)
                    return new AttendanceCell { BusinessDate = day, WorkshopId = workshopId, WorkingHours = 0, IsOffDay = isOffDay, IsResigned = true };
                var standard = new AttendanceCell { BusinessDate = day, WorkshopId = workshopId, WorkingHours = isOffDay ? 0 : StandardHours, IsOffDay = isOffDay, IsConfirmed = true };
                if (staged.TryGetValue((employee.Id, day), out var change))
                    return change.Revert ? standard : new AttendanceCell { BusinessDate = day, WorkingHours = change.Hours, WorkshopId = change.WorkshopId, LeaveType = isOffDay ? LeaveType.None : (LeaveType)change.LeaveType, LeaveHours = isOffDay ? 0 : change.LeaveHours, IsOffDay = isOffDay, IsConfirmed = true };
                if (entries.TryGetValue((employee.Id, day), out var entry))
                    return new AttendanceCell { BusinessDate = day, WorkingHours = entry.WorkingHours, WorkshopId = entry.WorkshopId, LeaveType = isOffDay ? LeaveType.None : entry.LeaveType, LeaveHours = isOffDay ? 0 : entry.LeaveHours, IsOffDay = isOffDay, IsConfirmed = true };
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

    // Shows whole Sunday–Saturday weeks around the month, like the Excel sheets, so weekly totals are complete.
    void SetCalendarRange()
    {
        var current = BusinessDateService.Current();
        MonthStart = DateOnly.TryParseExact(Month + "-01", "yyyy-MM-dd", out var parsed) ? new DateOnly(parsed.Year, parsed.Month, 1) : new DateOnly(current.Year, current.Month, 1);
        Month = MonthStart.ToString("yyyy-MM");
        var monthEnd = MonthStart.AddMonths(1).AddDays(-1);
        var start = MonthStart.AddDays(-(int)MonthStart.DayOfWeek);
        var end = monthEnd.AddDays(6 - (int)monthEnd.DayOfWeek);
        Days = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i)).ToList();
        WeekGroups = Enumerable.Range(0, Days.Count / 7).Select(w => new WeekGroup(Days[w * 7], Days[w * 7 + 6], w * 7, 7)).ToList();
        if (!HasScope) return;
        var workCalendar = db.WorkCalendars.Where(c => c.WorkshopId == WorkshopId && c.WorkDate >= start && c.WorkDate <= end).Select(c => new { c.WorkDate, c.IsWorkingDay }).ToDictionary(c => c.WorkDate, c => c.IsWorkingDay);
        // An imported calendar always takes precedence. Until it is imported, each shift follows its 4-work / 2-OFF cycle.
        OffDays = Days.Where(day => workCalendar.TryGetValue(day, out var isWorkingDay) ? !isWorkingDay : IsDefaultOffDay(Shift, day)).ToHashSet();
    }

    internal static bool IsDefaultOffDay(string shift, DateOnly day)
    {
        if (!ShiftReferenceWorkingDays.TryGetValue(shift, out var referenceWorkingDay)) return false;
        var cycleDay = ((day.DayNumber - referenceWorkingDay.DayNumber) % 6 + 6) % 6;
        return cycleDay >= 4;
    }

    async Task Options()
    {
        var active = db.Employees.Where(e => e.EmploymentState == EmploymentState.Active);
        WorkshopOptions = await cache.GetOrCreateAsync(WorkshopOptionsCacheKey, async entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(5);
            return await active.Select(e => e.CurrentWorkshop!).Where(w => w != null).Distinct().OrderBy(w => w.Name).Select(w => new SelectListItem(w.Name, w.Id.ToString())).ToListAsync();
        }) ?? [];
        WorkshopName = AllowedWorkshops.FirstOrDefault(o => o.Value == WorkshopId?.ToString())?.Text ?? WorkshopOptions.FirstOrDefault(o => o.Value == WorkshopId?.ToString())?.Text ?? "";
        var shifts = active.AsQueryable(); if (WorkshopId is int workshopId) shifts = shifts.Where(e => e.CurrentWorkshopId == workshopId);
        ShiftOptions = await shifts.Select(e => e.Shift).Distinct().OrderBy(s => s).Select(s => new SelectListItem(s, s)).ToListAsync();
    }
}
