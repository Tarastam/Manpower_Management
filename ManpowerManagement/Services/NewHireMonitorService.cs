using ManpowerManagement.Data;
using ManpowerManagement.Models;
using Microsoft.EntityFrameworkCore;
using AttendanceIndex = ManpowerManagement.Pages.Attendance.IndexModel;

namespace ManpowerManagement.Services;

// Who is looking at the new hire pages. HR (Admin or Approver) sees everything; a checked-in leader only sees
// the workshops under their own line; everyone else is sent to the leader check-in.
public sealed class NewHireAccess
{
    public bool IsAdmin { get; init; }
    public bool IsHr { get; init; }
    public LeaderSession? Leader { get; init; }
    // Everyone who can open the new hire pages sees every line. Scope only limits what a leader may change
    // (output, probation decisions): null means every workshop.
    public HashSet<int>? Scope { get; init; }
    public string Actor { get; init; } = "";
    public bool CanView => IsHr || Leader is not null;
    // Leaders key output and decide probation; HR only acknowledges. Admin can do both.
    public bool CanWrite => IsAdmin || Leader is not null;
    public bool InScope(int workshopId) => Scope is null || Scope.Contains(workshopId);
}

public enum NewHirePhase { NotStarted, Evaluating, DecisionSoon, DecisionOverdue, Extended, Passed, NotPassed }

// A new hire is monitored for 90 days. The probation decision is due before day 50, and an extension may run
// up to day 119 in total. Day 0 is the start date.
public sealed class NewHireMonitorService(AppDbContext db, LeaderSessionService leaderSessions, AttendanceStagingStore staging)
{
    public const int DecisionDay = 50;
    public const int ProbationDays = 90;
    public const int MaxProbationDays = 119;
    public const int ExtensionWarningDays = 10;
    static decimal StandardHours => AttendanceIndex.StandardHours;

    public record Milestones(DateOnly Start, DateOnly DecisionDue, DateOnly ProbationEnd, DateOnly MaxEnd);
    public static Milestones MilestonesFor(DateOnly start) => new(start, start.AddDays(DecisionDay), start.AddDays(ProbationDays), start.AddDays(MaxProbationDays));

    public record NewHireRow(int Id, string Code, string Name, string Workshop, int WorkshopId, string Shift, string Process,
        DateOnly StartDate, int TenureDays, NewHirePhase Phase, DateOnly Deadline, int DaysToDeadline,
        int ScheduledDays, int PresentDays, decimal? AttendanceRate, int ConsecutiveAbsent, decimal LeaveRate,
        decimal? RecentKps, List<string> RiskFlags, ProbationReview? Latest)
    {
        public int TenureWeek => TenureDays < 0 ? 0 : TenureDays / 7 + 1;
        public bool AtRisk => RiskFlags.Count > 0;
    }
    // Share of people who have reached a tenure day and were still employed on it.
    public record Retention(int Eligible, int Retained)
    {
        public decimal? Percent => Eligible == 0 ? null : Retained * 100m / Eligible;
    }
    public record CohortRow(string Month, int Hired, int StillEmployed, int Resigned, Retention Day30, Retention Day60, Retention Day90, Retention Day119);
    public record DayItem(DateOnly Date, bool Scheduled, string Code);
    public record OutputPoint(DateOnly Date, decimal? Kps);
    // A day of leave of any kind. It needs the leader's comment on whether it affects the probation decision.
    public record LeaveItem(DateOnly Date, LeaveType Type, decimal Hours, NewHireNote? Review);

    public sealed class Monitor
    {
        public DateOnly Today { get; init; }
        public NewHireRiskSetting Settings { get; init; } = new();
        public List<Workshop> Workshops { get; init; } = [];
        public int? WorkshopId { get; init; }
        public string WorkshopLabel { get; init; } = "All lines";
        public List<NewHireRow> Rows { get; init; } = [];
        public int DecisionSoon { get; init; }
        public int DecisionOverdue { get; init; }
        public int AtRisk { get; init; }
        public decimal? NewHireAbsenceRate { get; init; }
        public decimal? PermanentAbsenceRate { get; init; }
        public decimal? Retention90 { get; init; }
        public List<CohortRow> Cohorts { get; init; } = [];
    }

    public sealed class Detail
    {
        public required Employee Employee { get; init; }
        public required NewHireRow Row { get; init; }
        public List<ProbationReview> Reviews { get; init; } = [];
        public List<DayItem> Days { get; init; } = [];
        public List<OutputPoint> Points { get; init; } = [];
        public required Milestones Milestones { get; init; }
        // Range of dates a leader may pick when extending probation; empty when no extension is left.
        public DateOnly? ExtendFrom { get; init; }
        public DateOnly? ExtendTo { get; init; }
        public bool DecisionOpen { get; init; }
        public List<LeaveItem> Leaves { get; init; } = [];
        public List<NewHireNote> LateNotes { get; init; } = [];
        // Leaders and Admin can comment for employees in their own line.
        public bool CanNote { get; init; }
        // A leader may only decide for their own line; Admin for every line.
        public bool CanDecide { get; init; }
    }

    sealed class Ctx
    {
        public DateOnly Today;
        public NewHireRiskSetting Settings = new();
        public Dictionary<int, Workshop> Workshops = [];
        public Dictionary<int, List<ProbationReview>> Reviews = [];
        public Dictionary<int, List<AttendanceEntry>> Entries = [];
        public Dictionary<int, List<NewHireOutput>> Outputs = [];
        public Dictionary<int, List<NewHireNote>> Notes = [];
        public Dictionary<(int WorkshopId, DateOnly Day), bool> Calendar = [];
    }

    public async Task<NewHireAccess> ResolveAccessAsync(HttpContext http)
    {
        var user = http.User;
        var isAdmin = user.IsInRole(Roles.Admin);
        var isHr = isAdmin || user.IsInRole(Roles.Approver);
        var leader = await leaderSessions.ReadAsync(http, db);
        HashSet<int>? scope = null;
        if (!isHr && leader is not null)
            scope = WorkforceDashboardService.DescendantIds(await db.Workshops.AsNoTracking().ToListAsync(), leader.WorkshopId);
        return new NewHireAccess
        {
            IsAdmin = isAdmin, IsHr = isHr, Leader = leader, Scope = scope,
            Actor = isHr ? user.Identity?.Name ?? "HR" : leader?.Actor ?? ""
        };
    }

    // Active employees who were tagged "New Employee" when they were added and have not passed probation yet
    // (HR acknowledging a pass makes them Permanent). Age is counted from the start date, never from when the record was added.
    public async Task<List<Employee>> MonitoredEmployeesAsync(NewHireAccess access, int? workshopId, DateOnly today, bool forWriting = false)
    {
        var scope = await ScopeAsync(access, workshopId, forWriting);
        var query = db.Employees.AsNoTracking().Where(e => e.EmploymentState == EmploymentState.Active && e.IsNewEmployee && e.EmploymentType == EmploymentType.NewComer);
        if (scope is not null) query = query.Where(e => scope.Contains(e.CurrentWorkshopId));
        return await query.ToListAsync();
    }

    // Reading is never limited by the leader's line; writing is.
    async Task<HashSet<int>?> ScopeAsync(NewHireAccess access, int? workshopId, bool forWriting = false)
    {
        var scope = forWriting ? access.Scope : null;
        if (workshopId is int id)
        {
            var picked = WorkforceDashboardService.DescendantIds(await db.Workshops.AsNoTracking().ToListAsync(), id);
            scope = scope is null ? picked : scope.Intersect(picked).ToHashSet();
        }
        return scope;
    }

    public async Task<Monitor> LoadAsync(NewHireAccess access, int? workshopId)
    {
        var today = BusinessDateService.Current();
        var workshops = await db.Workshops.AsNoTracking().OrderBy(w => w.Name).ToListAsync();
        // An unknown workshop id (e.g. an old bookmark) means every line.
        if (workshopId is int picked && workshops.All(w => w.Id != picked)) workshopId = null;

        var employees = await MonitoredEmployeesAsync(access, workshopId, today);
        var ctx = await BuildContextAsync(employees, today);
        var rows = employees.Select(e => BuildRow(e, ctx))
            .OrderBy(r => PhaseOrder(r.Phase)).ThenByDescending(r => r.RiskFlags.Count).ThenBy(r => r.StartDate).ThenBy(r => r.Code).ToList();

        var (newHireAbsence, permanentAbsence) = await AbsenceRatesAsync(access, workshopId, rows, ctx, today);
        var cohorts = await CohortsAsync(access, workshopId, today);

        var eligible90 = cohorts.Sum(c => c.Day90.Eligible);
        return new Monitor
        {
            Today = today, Settings = ctx.Settings, Workshops = workshops, WorkshopId = workshopId,
            WorkshopLabel = workshopId is int wid ? workshops.First(w => w.Id == wid).Name : "All lines",
            Rows = rows,
            DecisionSoon = rows.Count(r => r.Phase == NewHirePhase.DecisionSoon),
            DecisionOverdue = rows.Count(r => r.Phase == NewHirePhase.DecisionOverdue),
            AtRisk = rows.Count(r => r.AtRisk),
            NewHireAbsenceRate = newHireAbsence, PermanentAbsenceRate = permanentAbsence,
            Retention90 = Pct(cohorts.Sum(c => c.Day90.Retained), eligible90),
            Cohorts = cohorts
        };
    }

    static int PhaseOrder(NewHirePhase p) => p switch
    {
        NewHirePhase.DecisionOverdue => 0, NewHirePhase.DecisionSoon => 1, NewHirePhase.Evaluating => 2,
        NewHirePhase.Extended => 3, NewHirePhase.NotStarted => 4, NewHirePhase.Passed => 5, _ => 6
    };

    static decimal? Pct(int part, int whole) => whole == 0 ? null : part * 100m / whole;

    public async Task<Detail?> LoadDetailAsync(NewHireAccess access, int employeeId)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId);
        if (employee is null) return null;
        var today = BusinessDateService.Current();
        var ctx = await BuildContextAsync([employee], today);
        var row = BuildRow(employee, ctx);
        var reviews = ctx.Reviews.GetValueOrDefault(employee.Id)?.OrderByDescending(r => r.DecidedAtUtc).ThenByDescending(r => r.Id).ToList() ?? [];
        var entries = ctx.Entries.GetValueOrDefault(employee.Id) ?? [];
        var days = Enumerable.Range(0, 35).Select(i => today.AddDays(-i)).Where(d => d >= employee.StartDate)
            .Select(d =>
            {
                var scheduled = IsScheduled(ctx, employee, d);
                var entry = entries.FirstOrDefault(e => e.BusinessDate == d);
                // No entry on a working day means the standard hours: everyone counts as present unless a change was keyed.
                var code = entry is null ? (scheduled && d <= today ? StandardHours.ToString("0.##") : scheduled ? "—" : "OFF")
                    : entry.LeaveType != LeaveType.None ? AttendanceIndex.LeaveCodes.GetValueOrDefault(entry.LeaveType, "OL")
                    : entry.WorkingHours > 0 ? entry.WorkingHours.ToString("0.##") : "OFF";
                return new DayItem(d, scheduled, code);
            }).ToList();
        var outputs = ctx.Outputs.GetValueOrDefault(employee.Id)?.ToDictionary(o => o.BusinessDate) ?? [];
        var last = today < MilestonesFor(employee.StartDate).MaxEnd ? today : MilestonesFor(employee.StartDate).MaxEnd;
        var points = employee.StartDate > last ? [] : Enumerable.Range(0, last.DayNumber - employee.StartDate.DayNumber + 1)
            .Select(i => employee.StartDate.AddDays(i))
            .Select(d => new OutputPoint(d, outputs.TryGetValue(d, out var o) ? o.Kps : null)).ToList();

        var notes = ctx.Notes.GetValueOrDefault(employee.Id) ?? [];
        var leaves = entries.Where(x => x.LeaveType != LeaveType.None).GroupBy(x => x.BusinessDate).OrderByDescending(g => g.Key)
            .Select(g => new LeaveItem(g.Key, g.First().LeaveType, g.Sum(x => x.LeaveHours), notes.FirstOrDefault(n => n.Kind == NewHireNoteKind.Leave && n.BusinessDate == g.Key))).ToList();

        var m = MilestonesFor(employee.StartDate);
        var previousEnd = reviews.FirstOrDefault() is { Decision: ProbationDecision.Extended } ext ? ext.ExtendedUntil ?? m.ProbationEnd : m.ProbationEnd;
        var from = previousEnd > today ? previousEnd.AddDays(1) : today.AddDays(1);
        DateOnly? extendFrom = from <= m.MaxEnd ? from : null;
        // A decision is open until HR has acknowledged it, except that an extension stays open for the final decision.
        var open = employee.EmploymentState == EmploymentState.Active && employee.IsNewEmployee && employee.EmploymentType == EmploymentType.NewComer &&
            (reviews.Count == 0 || reviews[0].Decision == ProbationDecision.Extended);
        return new Detail
        {
            Employee = employee, Row = row, Reviews = reviews, Days = days, Points = points, Milestones = m,
            ExtendFrom = extendFrom, ExtendTo = extendFrom is null ? null : m.MaxEnd, DecisionOpen = open,
            Leaves = leaves, LateNotes = notes.Where(n => n.Kind == NewHireNoteKind.Late).OrderByDescending(n => n.BusinessDate).ToList(),
            CanNote = access.CanWrite && access.InScope(employee.CurrentWorkshopId),
            CanDecide = open && access.CanWrite && access.InScope(employee.CurrentWorkshopId)
        };
    }

    async Task<Ctx> BuildContextAsync(List<Employee> employees, DateOnly today)
    {
        var ctx = new Ctx { Today = today };
        ctx.Settings = await db.NewHireRiskSettings.AsNoTracking().FirstOrDefaultAsync() ?? new NewHireRiskSetting();
        ctx.Workshops = (await db.Workshops.AsNoTracking().ToListAsync()).ToDictionary(w => w.Id);
        if (employees.Count == 0) return ctx;
        var ids = employees.Select(e => e.Id).ToList();
        var from = employees.Min(e => e.StartDate);
        ctx.Reviews = (await db.ProbationReviews.AsNoTracking().Where(r => ids.Contains(r.EmployeeId)).ToListAsync())
            .GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        // Future dates only hold leave keyed in advance, so they are not attendance yet.
        var idSet = ids.ToHashSet();
        var stored = await db.AttendanceEntries.AsNoTracking()
            .Where(e => ids.Contains(e.EmployeeId) && e.IsConfirmed && e.BusinessDate >= from && e.BusinessDate <= today).ToListAsync();
        // Changes still waiting for the 07:00 / 19:00 copy to SQL count too.
        ctx.Entries = staging.Overlay(stored.Where(e => idSet.Contains(e.EmployeeId)), from, today, c => idSet.Contains(c.EmployeeId))
            .Where(e => idSet.Contains(e.EmployeeId))
            .GroupBy(e => e.EmployeeId).ToDictionary(g => g.Key, g => g.OrderBy(e => e.BusinessDate).ToList());
        ctx.Notes = (await db.NewHireNotes.AsNoTracking().Where(n => ids.Contains(n.EmployeeId)).ToListAsync())
            .GroupBy(n => n.EmployeeId).ToDictionary(g => g.Key, g => g.OrderBy(n => n.BusinessDate).ToList());
        ctx.Outputs = (await db.NewHireOutputs.AsNoTracking().Where(o => ids.Contains(o.EmployeeId) && o.BusinessDate <= today).ToListAsync())
            .GroupBy(o => o.EmployeeId).ToDictionary(g => g.Key, g => g.OrderBy(o => o.BusinessDate).ToList());
        var workshopIds = employees.Select(e => e.CurrentWorkshopId).Distinct().ToList();
        ctx.Calendar = (await db.WorkCalendars.AsNoTracking().Where(c => workshopIds.Contains(c.WorkshopId) && c.WorkDate >= from && c.WorkDate <= today).ToListAsync())
            .ToDictionary(c => (c.WorkshopId, c.WorkDate), c => c.IsWorkingDay);
        return ctx;
    }

    static bool IsScheduled(Ctx ctx, Employee e, DateOnly day) =>
        ctx.Calendar.TryGetValue((e.CurrentWorkshopId, day), out var working) ? working : !AttendanceIndex.IsDefaultOffDay(e.Shift, day);

    NewHireRow BuildRow(Employee e, Ctx ctx)
    {
        var today = ctx.Today;
        var s = ctx.Settings;
        var m = MilestonesFor(e.StartDate);
        var tenure = today.DayNumber - e.StartDate.DayNumber;
        var latest = ctx.Reviews.GetValueOrDefault(e.Id)?.OrderByDescending(r => r.DecidedAtUtc).ThenByDescending(r => r.Id).FirstOrDefault();
        var deadline = latest is { Decision: ProbationDecision.Extended } ? latest.ExtendedUntil ?? m.ProbationEnd : m.DecisionDue;
        var toDeadline = deadline.DayNumber - today.DayNumber;
        var phase = tenure < 0 ? NewHirePhase.NotStarted
            : latest is null ? (today >= m.DecisionDue ? NewHirePhase.DecisionOverdue : tenure >= Math.Min(s.DecisionWarningDay, DecisionDay - 1) ? NewHirePhase.DecisionSoon : NewHirePhase.Evaluating)
            : latest.Decision switch
            {
                ProbationDecision.Passed => NewHirePhase.Passed,
                ProbationDecision.NotPassed => NewHirePhase.NotPassed,
                _ => today >= deadline ? NewHirePhase.DecisionOverdue : toDeadline <= ExtensionWarningDays ? NewHirePhase.DecisionSoon : NewHirePhase.Extended
            };

        var entries = ctx.Entries.GetValueOrDefault(e.Id) ?? [];
        var flags = new List<string>();
        // A new day counts from its start: everyone is present for the standard hours unless a change says otherwise.
        var lastDay = today;
        int Scheduled(DateOnly from, DateOnly to)
        {
            var count = 0;
            for (var d = from < e.StartDate ? e.StartDate : from; d <= to; d = d.AddDays(1)) if (IsScheduled(ctx, e, d)) count++;
            return count;
        }
        var scheduled = Scheduled(e.StartDate, lastDay);
        var absentDays = entries.Where(x => x.WorkingHours == 0 && x.BusinessDate >= e.StartDate && x.BusinessDate <= lastDay && IsScheduled(ctx, e, x.BusinessDate)).Select(x => x.BusinessDate).Distinct().Count();
        var presentDays = Math.Max(0, scheduled - absentDays);
        decimal? attendance = scheduled == 0 ? null : presentDays * 100m / scheduled;

        // Every day of leave, whatever the type, asks the leader to reconsider probation until a comment is saved for it.
        if (phase is not (NewHirePhase.Passed or NewHirePhase.NotPassed))
        {
            var reviewed = (ctx.Notes.GetValueOrDefault(e.Id) ?? []).Where(n => n.Kind == NewHireNoteKind.Leave).Select(n => n.BusinessDate).ToHashSet();
            var pending = entries.Where(x => x.LeaveType != LeaveType.None && !reviewed.Contains(x.BusinessDate)).OrderBy(x => x.BusinessDate).ToList();
            if (pending.Count > 0)
                flags.Add($"Leave to review ×{pending.Count}: latest {NewHireText.Date(pending[^1].BusinessDate)} ({NewHireText.LeaveLabel(pending[^1].LeaveType)}) – consider probation");
        }

        var absentRun = entries.OrderByDescending(x => x.BusinessDate).TakeWhile(x => x.LeaveType == LeaveType.Absent && x.WorkingHours == 0).Count();
        if (absentRun >= s.ConsecutiveAbsentDays) flags.Add($"Absent {absentRun} days in a row");

        var windowStart = e.StartDate > today.AddDays(-s.LeaveRateWindowDays) ? e.StartDate : today.AddDays(-s.LeaveRateWindowDays);
        var windowScheduled = Scheduled(windowStart, lastDay);
        var windowLeave = entries.Where(x => x.LeaveType != LeaveType.None && x.BusinessDate >= windowStart && x.BusinessDate <= lastDay && IsScheduled(ctx, e, x.BusinessDate)).Sum(x => x.LeaveHours);
        var leaveRate = windowScheduled == 0 ? 0 : windowLeave * 100m / (windowScheduled * StandardHours);
        // Three scheduled days is the minimum for a rate to mean anything.
        if (windowScheduled >= 3 && leaveRate > s.LeaveRatePercent) flags.Add($"Leave {leaveRate:0.#}% in last {s.LeaveRateWindowDays} days");

        if (presentDays == 0 && scheduled > 0)
        {
            var first = new List<DateOnly>();
            for (var d = e.StartDate; d <= lastDay && first.Count < s.NoShowDays; d = d.AddDays(1)) if (IsScheduled(ctx, e, d)) first.Add(d);
            var keyed = first.Select(d => entries.FirstOrDefault(x => x.BusinessDate == d)).Where(x => x is not null).ToList();
            if (keyed.Count > 0 && keyed.All(x => x!.LeaveType == LeaveType.Absent && x.WorkingHours == 0))
                flags.Add($"No-show: absent all first {keyed.Count} day(s)");
        }

        var outputs = ctx.Outputs.GetValueOrDefault(e.Id) ?? [];
        var recent = outputs.Where(o => o.BusinessDate >= today.AddDays(-6)).ToList();
        decimal? recentKps = recent.Count == 0 ? null : recent.Average(o => o.Kps);

        return new NewHireRow(e.Id, e.EmployeeId, e.FullName, ctx.Workshops.GetValueOrDefault(e.CurrentWorkshopId)?.Name ?? "Unknown", e.CurrentWorkshopId,
            e.Shift, e.Process, e.StartDate, tenure, phase, deadline, toDeadline, scheduled, presentDays, attendance, absentRun, leaveRate,
            recentKps, flags, latest);
    }

    // Leave hours divided by scheduled person-days over the last 30 days: new hires against everyone else.
    // Everyone counts as scheduled for the standard hours on a working day, so the denominator does not depend on how much was keyed.
    async Task<(decimal? NewHire, decimal? Permanent)> AbsenceRatesAsync(NewHireAccess access, int? workshopId, List<NewHireRow> rows, Ctx ctx, DateOnly today)
    {
        var from = today.AddDays(-30);
        int ScheduledDays(int workshopId, string shift, DateOnly start, DateOnly? end)
        {
            var count = 0;
            for (var d = start > from ? start : from; d <= (end is DateOnly e && e < today ? e : today); d = d.AddDays(1))
                if (EffectiveAttendance.IsScheduled(ctx.Calendar, workshopId, shift, d)) count++;
            return count;
        }
        var rowIds = rows.Select(r => r.Id).ToHashSet();
        var newHireDays = rows.Sum(r => ScheduledDays(r.WorkshopId, r.Shift, r.StartDate, null));
        var newHireLeave = ctx.Entries.Where(kv => rowIds.Contains(kv.Key)).SelectMany(kv => kv.Value)
            .Where(x => x.BusinessDate >= from && x.LeaveType != LeaveType.None).Sum(x => x.LeaveHours);
        decimal? newHire = newHireDays == 0 ? null : newHireLeave * 100m / (newHireDays * StandardHours);

        var scope = await ScopeAsync(access, workshopId);
        var people = db.Employees.AsNoTracking().Where(e => !e.IsNewEmployee && e.StartDate <= today && (e.ResignedDate == null ? e.EmploymentState == EmploymentState.Active : e.ResignedDate >= from));
        if (scope is not null) people = people.Where(e => scope.Contains(e.CurrentWorkshopId));
        var permanent = await people.Select(e => new { e.Id, e.CurrentWorkshopId, e.Shift, e.StartDate, e.ResignedDate }).ToListAsync();
        var calendar = (await db.WorkCalendars.AsNoTracking().Where(c => c.WorkDate >= from && c.WorkDate <= today).ToListAsync())
            .ToDictionary(c => (c.WorkshopId, c.WorkDate), c => c.IsWorkingDay);
        var permanentDays = 0;
        foreach (var person in permanent)
            for (var d = person.StartDate > from ? person.StartDate : from; d <= (person.ResignedDate is DateOnly r && r < today ? r : today); d = d.AddDays(1))
                if (EffectiveAttendance.IsScheduled(calendar, person.CurrentWorkshopId, person.Shift, d)) permanentDays++;
        var leaveQuery = db.AttendanceEntries.AsNoTracking().Where(x => x.IsConfirmed && x.BusinessDate >= from && x.BusinessDate <= today && x.LeaveHours > 0 && !x.Employee!.IsNewEmployee);
        if (scope is not null) leaveQuery = leaveQuery.Where(x => scope.Contains(x.Employee!.CurrentWorkshopId));
        var permanentLeave = await leaveQuery.SumAsync(x => (decimal?)x.LeaveHours) ?? 0;
        // Pending changes: take out the stored leave they replace and add the leave they bring.
        var pending = staging.PendingCells(c => c.Date >= from && c.Date <= today);
        if (pending.Count > 0)
        {
            var permanentIds = permanent.Select(p => p.Id).ToHashSet();
            var pendingIds = pending.Select(c => c.EmployeeId).Where(permanentIds.Contains).Distinct().ToList();
            if (pendingIds.Count > 0)
            {
                var keys = pending.Where(c => permanentIds.Contains(c.EmployeeId)).Select(c => (c.EmployeeId, c.Date, c.Shift)).ToHashSet();
                var replaced = (await db.AttendanceEntries.AsNoTracking()
                    .Where(x => x.IsConfirmed && x.BusinessDate >= from && x.BusinessDate <= today && x.LeaveHours > 0 && pendingIds.Contains(x.EmployeeId)).ToListAsync())
                    .Where(x => keys.Contains((x.EmployeeId, x.BusinessDate, x.Shift))).Sum(x => x.LeaveHours);
                var added = pending.Where(c => !c.Revert && c.LeaveType != 0 && permanentIds.Contains(c.EmployeeId)).Sum(c => c.LeaveHours);
                permanentLeave = Math.Max(0, permanentLeave - replaced + added);
            }
        }
        return (newHire, permanentDays == 0 ? null : permanentLeave * 100m / (permanentDays * StandardHours));
    }

    // Retention by start month for the last six months: the share of each cohort still employed at day 30/60/90/119.
    // Only people who have already reached that day count, so young cohorts show a dash instead of a misleading 100%.
    async Task<List<CohortRow>> CohortsAsync(NewHireAccess access, int? workshopId, DateOnly today)
    {
        var scope = await ScopeAsync(access, workshopId);
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-5);
        var query = db.Employees.AsNoTracking().Where(e => e.IsNewEmployee && e.StartDate >= firstMonth && e.StartDate <= today);
        if (scope is not null) query = query.Where(e => scope.Contains(e.CurrentWorkshopId));
        var hires = await query.Select(e => new { e.StartDate, e.ResignedDate }).ToListAsync();
        var rows = new List<CohortRow>();
        foreach (var month in Enumerable.Range(0, 6).Select(i => firstMonth.AddMonths(i)))
        {
            var label = month.ToString("yyyy-MM");
            var cohort = hires.Where(h => h.StartDate.Year == month.Year && h.StartDate.Month == month.Month).ToList();
            Retention At(int day)
            {
                var eligible = cohort.Where(h => h.StartDate.AddDays(day) <= today).ToList();
                return new Retention(eligible.Count, eligible.Count(h => h.ResignedDate is null || h.ResignedDate > h.StartDate.AddDays(day)));
            }
            var resigned = cohort.Count(h => h.ResignedDate is not null && h.ResignedDate <= today);
            rows.Add(new CohortRow(label, cohort.Count, cohort.Count - resigned, resigned, At(30), At(60), At(90), At(MaxProbationDays)));
        }
        return rows.Where(r => r.Hired > 0).ToList();
    }
}
