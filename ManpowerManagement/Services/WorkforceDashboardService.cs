using ManpowerManagement.Data;
using ManpowerManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Services;

public sealed class WorkforceDashboardService(AppDbContext db, AttendanceStagingStore staging)
{
    public record LeaveSummary(LeaveType Type, string Name, decimal Hours, decimal Rate);
    public record HighLeaveEmployee(int Id, string Code, string Name, string Shift, string Workshop, int WorkshopId, decimal LeaveHours, int ScheduledDays, decimal Rate);
    public record WorkshopSummary(int Id, string Name, int Count);
    public record ResignedEmployee(string Code, string Name, string Workshop, DateOnly ResignedDate);
    public enum ManpowerRowKind { Line, Subtotal, Staff, DirectLabor, GrandSubtotal, GrandTotal }
    // Balance is blank for the 53T-55T staff rows, which have no target of their own to be measured against.
    public record ManpowerRow(string Label, int Target, int Actual, ManpowerRowKind Kind)
    {
        public int? Balance => Kind == ManpowerRowKind.Staff ? null : Actual - Target;
    }
    // One special group; the counts are people tagged with it in the Chemical (First) and Assembly (Second) line.
    public record SupportRow(string Label, string ColorHex, int First, int Second)
    {
        public int Total => First + Second;
    }
    // One choice of the period picker above the summary tables: MTD or a week of the month (Monday to Sunday, cut at the month
    // boundaries). AsOf is the day the headcount is taken on; a period that has not started yet is not Available.
    public record PeriodOption(string Key, string Label, string Range, DateOnly From, DateOnly AsOf, bool Available);
    public const string Mtd = "mtd";
    public sealed class ManpowerSummary
    {
        public string Period { get; init; } = Mtd;
        public string PeriodLabel { get; init; } = "MTD";
        public DateOnly? AsOf { get; init; }
        public List<PeriodOption> Periods { get; init; } = [];
        public int FirstHeadcount { get; init; }
        public int SecondHeadcount { get; init; }
        public int TotalHeadcount => FirstHeadcount + SecondHeadcount;
        public List<SupportRow> Support { get; init; } = [];
        // Distinct people, so someone carrying two tags is taken off the headcount once.
        public int FirstSupport { get; init; }
        public int SecondSupport { get; init; }
        public int TotalSupport => FirstSupport + SecondSupport;
        public List<ManpowerRow> Rows { get; init; } = [];
    }
    public sealed class Report
    {
        public string Month { get; init; } = "";
        // The days the figures cover: PeriodStart up to (not including) PeriodEnd. A week stops at its last day or today.
        public DateOnly PeriodStart { get; init; }
        public DateOnly PeriodEnd { get; init; }
        public int? WorkshopId { get; init; }
        public string WorkshopLabel { get; init; } = "Total Production";
        public List<Workshop> Workshops { get; init; } = [];
        public int Headcount { get; init; }
        public int ScheduledPersonDays { get; init; }
        public int PresentPersonDays { get; init; }
        public decimal AttendanceRate { get; init; }
        public decimal AbsenceRate { get; init; }
        public decimal TurnoverRate { get; init; }
        public List<LeaveSummary> LeaveRows { get; init; } = [];
        public List<HighLeaveEmployee> HighLeaveEmployees { get; init; } = [];
        // Employees whose last working day falls in the selected month, newest first.
        public List<ResignedEmployee> ResignedEmployees { get; init; } = [];
        public List<WorkshopSummary> WorkshopRows { get; init; } = [];
        public ManpowerSummary Manpower { get; init; } = new();

        // The line under the summary tables and in the Excel download: which day the headcount is taken on.
        public string AsOfNote
        {
            get
            {
                if (Manpower.Period == Mtd && PeriodEnd == DateOnly.Parse(Month + "-01")) return "No reporting days yet";
                var asOf = Manpower.AsOf ?? PeriodEnd.AddDays(-1);
                var text = "Headcount as of " + asOf.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
                return Manpower.Period == Mtd ? text : $"{Manpower.PeriodLabel} · {text}";
            }
        }
    }

    // MTD first, then one entry per week of the month. today is the current business day.
    public static List<PeriodOption> PeriodOptions(DateOnly monthStart, DateOnly today)
    {
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var options = new List<PeriodOption>
        {
            new(Mtd, "MTD", $"{monthStart:dd/MM}-{(today < monthEnd ? today : monthEnd):dd/MM}", monthStart, today < monthEnd ? today : monthEnd, today >= monthStart)
        };
        var from = monthStart;
        for (var number = 1; from <= monthEnd; number++)
        {
            var to = from;
            while (to < monthEnd && to.DayOfWeek != DayOfWeek.Sunday) to = to.AddDays(1);
            options.Add(new($"w{number}", $"Week {number}", $"{from:dd/MM}-{to:dd/MM}", from, today < to ? today : to, today >= from));
            from = to.AddDays(1);
        }
        return options;
    }

    // An unknown key, or a week that has not started, falls back to MTD.
    public static PeriodOption ResolvePeriod(List<PeriodOption> options, string? key) =>
        options.FirstOrDefault(x => x.Available && x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? options[0];

    // Fixed headcount targets per production workshop (label shown, workshop name, target).
    static readonly (string Label, string Workshop, int Target)[] ChemicalTargets =
    [
        ("Pellet line", "Pellet Line", 33), ("Pellet Ass'y", "Pellet Assembly", 137),
        ("Anodization & Silicone", "Anodization", 69), ("Polymer & GR-AGP.", "Polymer", 252)
    ];
    static readonly (string Label, string Workshop, int Target)[] AssemblyTargets =
    [
        ("PSL-B", "PSLB", 440), ("PSL-A", "PSLA", 36), ("GPS-P2", "GPSP2", 18),
        ("FPS A", "FPSA", 135), ("FPS B", "FPSB", 81)
    ];
    const int ChemicalStaffTarget = 21, AssemblyStaffTarget = 23;

    static int? GradeNumber(string? jobGrade) =>
        int.TryParse(new string((jobGrade ?? "").Trim().TakeWhile(char.IsDigit).ToArray()), out var grade) ? grade : null;

    // Headcount on the last day of the period for the whole production floor, independent of the workshop filter.
    // Rows are split by job grade: 51T-52T per workshop, and 53T-55T for the whole Chemical / Assembly line.
    async Task<ManpowerSummary> BuildManpowerAsync(List<Workshop> workshops, DateOnly asOf, PeriodOption period, List<PeriodOption> periods)
    {
        var people = await db.Employees.AsNoTracking()
            .Where(x => x.StartDate <= asOf && (x.ResignedDate == null ? x.EmploymentState == EmploymentState.Active : x.ResignedDate >= asOf))
            .Select(x => new { x.CurrentWorkshopId, x.JobGrade, Groups = x.SpecialGroups.Select(g => g.SpecialGroup!.Name).ToList() })
            .ToListAsync();
        // Job grades look like "52T"; anything outside 51T-55T is not part of this table.
        var staff = people.Select(x => new { x.CurrentWorkshopId, Grade = GradeNumber(x.JobGrade), Keys = x.Groups })
            .Where(x => x.Grade is >= 51 and <= 55).ToList();
        var direct = staff.Where(x => x.Grade <= 52).ToList();

        HashSet<int> Scope(IEnumerable<string> names) => workshops
            .Where(w => names.Any(n => n.Equals(w.Name, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(w => DescendantIds(workshops, w.Id)).ToHashSet();
        int Count(HashSet<int> scope) => direct.Count(x => scope.Contains(x.CurrentWorkshopId));
        int Senior(HashSet<int> scope) => staff.Count(x => x.Grade >= 53 && scope.Contains(x.CurrentWorkshopId));
        List<ManpowerRow> Lines((string Label, string Workshop, int Target)[] defs) =>
            defs.Select(d => new ManpowerRow(d.Label, d.Target, Count(Scope([d.Workshop])), ManpowerRowKind.Line)).ToList();

        var firstScope = Scope(ChemicalTargets.Select(x => x.Workshop));
        var secondScope = Scope(AssemblyTargets.Select(x => x.Workshop));
        var chemical = Lines(ChemicalTargets);
        var assembly = Lines(AssemblyTargets);
        int Sum(List<ManpowerRow> rows, Func<ManpowerRow, int> pick) => rows.Sum(pick);
        var chemicalStaff = new ManpowerRow("TTL. 53T-55T", ChemicalStaffTarget, Senior(firstScope), ManpowerRowKind.Staff);
        var assemblyStaff = new ManpowerRow("TTL. 53T-55T", AssemblyStaffTarget, Senior(secondScope), ManpowerRowKind.Staff);
        var chemicalSub = new ManpowerRow("TTL. 51T-52T", Sum(chemical, r => r.Target), Sum(chemical, r => r.Actual), ManpowerRowKind.Subtotal);
        var assemblySub = new ManpowerRow("TTL. 51T-52T", Sum(assembly, r => r.Target), Sum(assembly, r => r.Actual), ManpowerRowKind.Subtotal);
        var bol = new ManpowerRow("TTL. BOL ( Direct Labor)", chemicalSub.Target + chemicalStaff.Target, chemicalSub.Actual + chemicalStaff.Actual, ManpowerRowKind.DirectLabor);
        var mol = new ManpowerRow("TTL. MOL-EOL ( Direct Labor)", assemblySub.Target + assemblyStaff.Target, assemblySub.Actual + assemblyStaff.Actual, ManpowerRowKind.DirectLabor);
        var rows = new List<ManpowerRow>();
        rows.AddRange(chemical);
        rows.AddRange([chemicalSub, chemicalStaff, bol]);
        rows.AddRange(assembly);
        rows.AddRange([assemblySub, assemblyStaff, mol,
            new ManpowerRow("Total. 51T-52T ( Direct Labor)", chemicalSub.Target + assemblySub.Target, chemicalSub.Actual + assemblySub.Actual, ManpowerRowKind.GrandSubtotal),
            new ManpowerRow("Total.51T-55T ( Direct Labor)", bol.Target + mol.Target, bol.Actual + mol.Actual, ManpowerRowKind.GrandTotal)]);

        // One support row per active special group, counted on the people in 51T-52T.
        var groups = await db.SpecialGroups.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync();
        var names = groups.Select(x => x.Name).ToHashSet();
        int Tagged(HashSet<int> scope, Func<string, bool> match) =>
            direct.Count(x => scope.Contains(x.CurrentWorkshopId) && x.Keys.Any(match));
        return new ManpowerSummary
        {
            Period = period.Key, PeriodLabel = period.Label, AsOf = asOf, Periods = periods,
            FirstHeadcount = chemicalSub.Actual, SecondHeadcount = assemblySub.Actual,
            Support = groups.Select(g => new SupportRow(g.Name, g.ColorHex, Tagged(firstScope, k => k == g.Name), Tagged(secondScope, k => k == g.Name))).ToList(),
            FirstSupport = Tagged(firstScope, names.Contains),
            SecondSupport = Tagged(secondScope, names.Contains),
            Rows = rows
        };
    }

    public static string NormalizeMonth(string? month) =>
        DateTime.TryParseExact(month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM")
            : BusinessDateService.Current().ToString("yyyy-MM");

    // period: "mtd" (default) or "w1", "w2" ... to cover just that week of the month. Every figure follows it except the
    // Resigned employees list, which always shows the whole month.
    public async Task<Report> LoadAsync(string? month, int? workshopId, string? period = null)
    {
        var selectedMonth = NormalizeMonth(month);
        var start = DateOnly.Parse(selectedMonth + "-01");
        var end = start.AddMonths(1);
        var today = BusinessDateService.Current();
        var mtdEnd = end <= today ? end : start > today ? start : today.AddDays(1);
        var periods = PeriodOptions(start, today);
        var chosen = ResolvePeriod(periods, period);
        var isMtd = chosen.Key == Mtd;
        var rangeStart = isMtd ? start : chosen.From;
        var periodEnd = isMtd ? mtdEnd : chosen.AsOf.AddDays(1);
        var workshops = await db.Workshops.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        if (workshopId is not null && workshops.All(x => x.Id != workshopId)) workshopId = null;
        var scope = workshopId is int id ? DescendantIds(workshops, id) : null;
        var workshopName = workshopId is int selectedId
            ? workshops.First(x => x.Id == selectedId).Name : "Total Production";

        // Everyone who was employed on at least one day of the period: current staff, plus people whose last working day
        // (ResignedDate) falls on or after the first day of the period. Resigned with no date cannot be placed in time.
        var employeeQuery = db.Employees.AsNoTracking()
            .Where(x => x.ResignedDate == null ? x.EmploymentState == EmploymentState.Active : x.ResignedDate >= rangeStart);
        if (scope is not null) employeeQuery = employeeQuery.Where(x => scope.Contains(x.CurrentWorkshopId));
        var employees = await employeeQuery.Select(x => new
        {
            x.Id, x.EmployeeId, x.FullName, x.CurrentWorkshopId, x.Shift, x.StartDate, x.ResignedDate
        }).ToListAsync();
        var employeeIds = employees.Select(x => x.Id).ToHashSet();
        // Headcount drops the day after the last working day: count those still employed on the last day of the period.
        var lastDayOfPeriod = periodEnd.AddDays(-1);
        var headcount = employees.Count(x => x.ResignedDate is null || x.ResignedDate >= lastDayOfPeriod);

        var entryQuery = db.AttendanceEntries.AsNoTracking()
            .Where(x => x.BusinessDate >= rangeStart && x.BusinessDate < periodEnd);
        if (scope is not null) entryQuery = entryQuery.Where(x => scope.Contains(x.WorkshopId));
        // Changes still waiting for the 07:00 / 19:00 copy to SQL are included, so the dashboard is current.
        var entries = staging.Overlay(await entryQuery.ToListAsync(), rangeStart, periodEnd.AddDays(-1), c => scope is null || scope.Contains(c.WorkshopId));
        var calendarQuery = db.WorkCalendars.AsNoTracking()
            .Where(x => x.WorkDate >= rangeStart && x.WorkDate < periodEnd);
        if (scope is not null) calendarQuery = calendarQuery.Where(x => scope.Contains(x.WorkshopId));
        var calendar = (await calendarQuery.ToListAsync())
            .ToDictionary(x => (x.WorkshopId, x.WorkDate), x => x.IsWorkingDay);
        var dates = Enumerable.Range(0, periodEnd.DayNumber - rangeStart.DayNumber)
            .Select(i => rangeStart.AddDays(i)).ToList();
        bool IsScheduled(int ws, string shift, DateOnly day) =>
            calendar.TryGetValue((ws, day), out var working)
                ? working : !Pages.Attendance.IndexModel.IsDefaultOffDay(shift, day);

        var scheduledDays = employees.ToDictionary(x => x.Id,
            x => dates.Count(day => day >= x.StartDate && (x.ResignedDate is null || day <= x.ResignedDate) && IsScheduled(x.CurrentWorkshopId, x.Shift, day)));
        var scheduledPersonDays = scheduledDays.Values.Sum();
        var employeeLookup = employees.ToDictionary(x => x.Id);
        // Employed from the start date through the last working day (both inclusive).
        bool Employed(int employeeId, DateOnly day) { var e = employeeLookup[employeeId]; return day >= e.StartDate && (e.ResignedDate is null || day <= e.ResignedDate); }
        // Everyone scheduled counts as present for the standard hours; only a keyed entry with no working hours takes a day away.
        var absentPersonDays = entries.Where(x => x.IsConfirmed && x.WorkingHours == 0 &&
                employeeIds.Contains(x.EmployeeId) &&
                Employed(x.EmployeeId, x.BusinessDate) &&
                IsScheduled(employeeLookup[x.EmployeeId].CurrentWorkshopId,
                    employeeLookup[x.EmployeeId].Shift, x.BusinessDate))
            .Select(x => (x.EmployeeId, x.BusinessDate)).Distinct().Count();
        var presentPersonDays = Math.Max(0, scheduledPersonDays - absentPersonDays);
        var leaveByEmployee = entries.Where(x => x.IsConfirmed && x.LeaveType != LeaveType.None &&
                employeeIds.Contains(x.EmployeeId) &&
                Employed(x.EmployeeId, x.BusinessDate) &&
                IsScheduled(employeeLookup[x.EmployeeId].CurrentWorkshopId,
                    employeeLookup[x.EmployeeId].Shift, x.BusinessDate))
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.LeaveHours));
        var standardHours = Pages.Attendance.IndexModel.StandardHours;
        var totalLeave = leaveByEmployee.Values.Sum();
        var leaveRows = Enum.GetValues<LeaveType>().Where(x => x != LeaveType.None)
            .Select(type =>
            {
                var hours = entries.Where(x => x.IsConfirmed && x.LeaveType == type &&
                        employeeIds.Contains(x.EmployeeId) &&
                        Employed(x.EmployeeId, x.BusinessDate) &&
                        IsScheduled(employeeLookup[x.EmployeeId].CurrentWorkshopId,
                            employeeLookup[x.EmployeeId].Shift, x.BusinessDate))
                    .Sum(x => x.LeaveHours);
                return new LeaveSummary(type, LeaveName(type), hours,
                    totalLeave == 0 ? 0 : hours / totalLeave * 100m);
            }).Where(x => x.Hours > 0).OrderByDescending(x => x.Hours).ToList();

        var highLeave = employees.Select(x =>
        {
            var hours = leaveByEmployee.GetValueOrDefault(x.Id);
            var days = scheduledDays[x.Id];
            var rate = days == 0 ? 0 : hours / (days * standardHours) * 100m;
            return new HighLeaveEmployee(x.Id, x.EmployeeId, x.FullName, x.Shift,
                workshops.FirstOrDefault(w => w.Id == x.CurrentWorkshopId)?.Name ?? "Unknown",
                x.CurrentWorkshopId, hours, days, rate);
        }).Where(x => x.Rate > 5m).OrderByDescending(x => x.Rate).ToList();
        var countsByWorkshop = highLeave.GroupBy(x => x.WorkshopId)
            .ToDictionary(g => g.Key, g => g.Count());
        var workshopRows = employees.Select(x => x.CurrentWorkshopId).Distinct()
            .Select(id => new WorkshopSummary(id,
                workshops.FirstOrDefault(w => w.Id == id)?.Name ?? "Unknown",
                countsByWorkshop.GetValueOrDefault(id)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name).ToList();
        var resignedQuery = db.Employees.AsNoTracking()
            .Where(x => x.ResignedDate >= start && x.ResignedDate < end);
        if (scope is not null) resignedQuery = resignedQuery.Where(x => scope.Contains(x.CurrentWorkshopId));
        var resignedPeople = (await resignedQuery.Select(x => new { x.EmployeeId, x.FullName, x.CurrentWorkshopId, x.ResignedDate }).ToListAsync())
            .OrderByDescending(x => x.ResignedDate).ThenBy(x => x.EmployeeId)
            .Select(x => new ResignedEmployee(x.EmployeeId, x.FullName,
                workshops.FirstOrDefault(w => w.Id == x.CurrentWorkshopId)?.Name ?? "Unknown", x.ResignedDate!.Value))
            .ToList();
        // Turnover counts the resignations inside the chosen period; the list above keeps the whole month.
        var resigned = isMtd ? resignedPeople.Count : resignedPeople.Count(x => x.ResignedDate >= rangeStart && x.ResignedDate < periodEnd);
        var turnoverDenominator = headcount + resigned / 2m;

        return new Report
        {
            Month = selectedMonth, PeriodStart = rangeStart, PeriodEnd = periodEnd, WorkshopId = workshopId,
            WorkshopLabel = workshopName, Workshops = workshops, Headcount = headcount,
            ScheduledPersonDays = scheduledPersonDays, PresentPersonDays = presentPersonDays,
            AttendanceRate = scheduledPersonDays == 0 ? 0 : presentPersonDays * 100m / scheduledPersonDays,
            AbsenceRate = scheduledPersonDays == 0 ? 0 : absentPersonDays * 100m / scheduledPersonDays,
            TurnoverRate = turnoverDenominator == 0 ? 0 : resigned * 100m / turnoverDenominator,
            ResignedEmployees = resignedPeople,
            LeaveRows = leaveRows, HighLeaveEmployees = highLeave, WorkshopRows = workshopRows,
            Manpower = await BuildManpowerAsync(workshops, lastDayOfPeriod, chosen, periods)
        };
    }

    public static HashSet<int> DescendantIds(List<Workshop> workshops, int rootId)
    {
        var result = new HashSet<int> { rootId };
        var pending = new Queue<int>();
        pending.Enqueue(rootId);
        while (pending.TryDequeue(out var parent))
            foreach (var child in workshops.Where(x => x.ParentId == parent && result.Add(x.Id)))
                pending.Enqueue(child.Id);
        return result;
    }

    static string LeaveName(LeaveType type) => type switch
    {
        LeaveType.AnnualLeave => "Annual leave",
        LeaveType.SickLeave => "Sick leave",
        LeaveType.BusinessLeave => "Business leave",
        LeaveType.OtherLeave => "Other leave",
        LeaveType.Absent => "Absent",
        LeaveType.MotherLeave => "Maternity leave",
        _ => type.ToString()
    };
}
