using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Pages.Attendance;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Services;

// Read-only data behind the public Report tab. Nothing here changes data, so anyone may view and download it.
public sealed class ReportService(AppDbContext db, AttendanceStagingStore staging)
{
    // Employees List is the only report that shows every shift; all others leave Day shift out.
    public static readonly string[] Shifts = ["A", "B", "C", "Day"];
    public const string Resigned = "Resigned";

    // One row per employee; Cells holds one text per day of the month (10.67, OFF, AL, SL4 ...), the same codes the attendance grid uses.
    public record AttendanceRow(string Code, string Name, string Workshop, string Process, string Shift, List<string> Cells,
        decimal WorkedHours, decimal LeaveHours, string Remarks, decimal ScheduledHours = 0, decimal AbsentHours = 0,
        List<string>? DayNotes = null, List<decimal>? WeekHours = null, List<LeaveType>? DayLeaves = null, List<bool>? DayEmployed = null);
    // Weeks run Monday to Sunday, cut at the month boundaries, so the first and last week of a month can be short.
    public record WeekSpan(DateOnly From, DateOnly To)
    {
        public string Label => $"{From:dd/MM}-{To:dd/MM}";
    }
    // Day shift is left out of every report except the employee list, so only the rotating shifts are reported.
    public static readonly string[] ReportShifts = ["A", "B", "C"];
    public static readonly LeaveType[] LeaveTypes = [LeaveType.AnnualLeave, LeaveType.SickLeave, LeaveType.BusinessLeave, LeaveType.OtherLeave, LeaveType.Absent, LeaveType.MotherLeave];
    // One workshop and shift on one day: Manpower is everyone employed that day (OFF days included); Leaves counts people per leave type.
    public record LeaveDayRow(DateOnly Date, string Workshop, string Shift, int Manpower, IReadOnlyDictionary<LeaveType, int> Leaves)
    {
        public int Of(LeaveType type) => Leaves.GetValueOrDefault(type);
        public int Total => Leaves.Values.Sum();
        public int TotalWithoutAl => Total - Of(LeaveType.AnnualLeave);    }
    public record LeavePoint(DateOnly Date, int Leave, int Manpower)
    {
        public decimal Ratio => Manpower == 0 ? 0 : (decimal)Leave / Manpower;
    }
    // Rows only cover days up to the current business day: later days have no attendance yet and would drag the average down.
    public record LeaveTrend(DateOnly MonthStart, List<DateOnly> Days, List<LeaveDayRow> Rows)
    {
        // One point per day: people on the chosen leave types over all manpower of that day.
        public List<LeavePoint> Series(Func<LeaveDayRow, int> leave) => Rows.GroupBy(r => r.Date).OrderBy(g => g.Key)
            .Select(g => new LeavePoint(g.Key, g.Sum(leave), g.Sum(r => r.Manpower))).Where(p => p.Manpower > 0).ToList();
        // The same trend cut down to the days from..to (both included), e.g. one week of the month.
        public LeaveTrend Between(DateOnly from, DateOnly to) => this with { Rows = [.. Rows.Where(r => r.Date >= from && r.Date <= to)] };
        // Month average of the daily %, as shown on the Absent Ratio report cards.
        public decimal Average(Func<LeaveDayRow, int> leave) => Series(leave) is { Count: > 0 } points ? points.Average(p => p.Ratio) : 0;
    }
    public record AttendanceTable(DateOnly MonthStart, List<DateOnly> Days, List<AttendanceRow> Rows, List<WeekSpan>? Weeks = null);
    // Hours and headcount of one closed day, workshop and shift. Headcount = people who worked that shift (Hours > 0).
    public record HoursRow(DateOnly Date, string Workshop, string Shift, decimal Total, decimal Indirect, int DirectCount = 0, int IndirectCount = 0)
    {
        public decimal Direct => Total - Indirect;
        public int TotalCount => DirectCount + IndirectCount;
    }
    // Telephone, accommodation and remark are left out on purpose: this report is public.
    public record EmployeeRow(string Code, string Name, string JobGrade, string Process, string Workshop, string Shift, string Status,
        DateOnly StartDate, DateOnly? ResignedDate, bool IsNewEmployee, string Groups);

    public static DateOnly ParseMonth(string? month)
    {
        if (DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", out var parsed)) return new DateOnly(parsed.Year, parsed.Month, 1);
        var current = BusinessDateService.Current();
        return new DateOnly(current.Year, current.Month, 1);
    }

    // The first of the month up to the current business day, unless the caller picked a range.
    public static (DateOnly From, DateOnly To) HoursRange(DateOnly? from, DateOnly? to)
    {
        var end = to ?? BusinessDateService.Current();
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        return start > end ? (end, start) : (start, end);
    }

    // Workshops that have at least one employee.
    public async Task<List<(int Id, string Name)>> WorkshopsAsync(CancellationToken cancellationToken = default) =>
        (await db.Workshops.AsNoTracking().Where(w => db.Employees.Any(e => e.CurrentWorkshopId == w.Id)).OrderBy(w => w.Name)
            .Select(w => new { w.Id, w.Name }).ToListAsync(cancellationToken)).Select(w => (w.Id, w.Name)).ToList();

    // Special groups to filter the monthly attendance by.
    public async Task<List<(int Id, string Name)>> SpecialGroupsAsync(CancellationToken cancellationToken = default) =>
        (await db.SpecialGroups.AsNoTracking().Where(g => g.IsActive).OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken)).Select(g => (g.Id, g.Name)).ToList();

    // Mirrors what the Attendance grid shows for the month: a change still waiting in the staging file, else the stored entry,
    // else the standard hours on a working day. Pregnant-group employees are kept on their own page, as in the grid, so they
    // are only listed when the Pregnant special group is picked: then their cells are the ones of that page (own entries,
    // 8 standard hours, Monday to Friday).
    // includePregnant lists them among everyone else, each with the cells of their own page (used by the leave trend, where ML only exists for them).
    // workshopScope (a workshop with its sub-workshops) takes the place of workshopId when given.
    // specialGroupId keeps only the members of that special group.
    public async Task<AttendanceTable> AttendanceMonthAsync(string? month, int? workshopId, string? shift, CancellationToken cancellationToken = default, IReadOnlyCollection<int>? workshopScope = null, int? specialGroupId = null, bool includePregnant = false)
    {
        var monthStart = ParseMonth(month);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var days = Enumerable.Range(0, monthEnd.Day).Select(i => monthStart.AddDays(i)).ToList();

        var pregnantOnly = specialGroupId is int pid && await db.SpecialGroups.AnyAsync(x => x.Id == pid && x.Name == PregnantModel.GroupName, cancellationToken);
        var roster = db.Employees.AsNoTracking().Where(e => ReportShifts.Contains(e.Shift) && e.StartDate <= monthEnd
            && (e.ResignedDate == null ? e.EmploymentState == EmploymentState.Active : e.ResignedDate >= monthStart)
            && (pregnantOnly || includePregnant || !e.SpecialGroups.Any(g => g.SpecialGroup!.Name == PregnantModel.GroupName)));
        if (workshopScope is not null) roster = roster.Where(e => workshopScope.Contains(e.CurrentWorkshopId));
        else if (workshopId is int w) roster = roster.Where(e => e.CurrentWorkshopId == w);
        if (!string.IsNullOrEmpty(shift)) roster = roster.Where(e => e.Shift == shift);
        if (specialGroupId is int g) roster = roster.Where(e => e.SpecialGroups.Any(x => x.SpecialGroupId == g));
        var employees = (await roster.Select(e => new { e.Id, e.EmployeeId, e.FullName, e.Process, e.Shift, e.CurrentWorkshopId, Workshop = e.CurrentWorkshop!.Name, e.StartDate, e.ResignedDate, Pregnant = e.SpecialGroups.Any(g => g.SpecialGroup!.Name == PregnantModel.GroupName) })
            .ToListAsync(cancellationToken))
            .OrderBy(e => e.Workshop, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Shift).ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        var ids = employees.Select(e => e.Id).ToHashSet();

        // Only exceptions are stored, so a month of entries is small; filtering by employee happens in memory.
        var entries = new Dictionary<(int, DateOnly, string), AttendanceEntry>();
        foreach (var entry in await db.AttendanceEntries.AsNoTracking().Where(e => e.IsConfirmed && e.BusinessDate >= monthStart && e.BusinessDate <= monthEnd).ToListAsync(cancellationToken))
            if (ids.Contains(entry.EmployeeId)) entries[(entry.EmployeeId, entry.BusinessDate, entry.Shift)] = entry;
        var staged = new Dictionary<(int, DateOnly, string), StagedCell>();
        foreach (var change in staging.PendingCells(c => c.Date >= monthStart && c.Date <= monthEnd && ids.Contains(c.EmployeeId)))
            staged[(change.EmployeeId, change.Date, change.Shift)] = change;

        var calendar = (await db.WorkCalendars.AsNoTracking().Where(c => c.WorkDate >= monthStart && c.WorkDate <= monthEnd).ToListAsync(cancellationToken))
            .ToDictionary(c => (c.WorkshopId, c.WorkDate), c => c.IsWorkingDay);

        // Remarks: stored ones, covered by any change still waiting in the staging file (an empty text clears it).
        var remarks = new Dictionary<(int, DateOnly), string>();
        foreach (var note in await db.AttendanceNotes.AsNoTracking().Where(n => n.BusinessDate >= monthStart && n.BusinessDate <= monthEnd).ToListAsync(cancellationToken))
            if (ids.Contains(note.EmployeeId)) remarks[(note.EmployeeId, note.BusinessDate)] = note.Text;
        foreach (var note in staging.PendingNotes(n => n.Date >= monthStart && n.Date <= monthEnd && ids.Contains(n.EmployeeId)))
            remarks[(note.EmployeeId, note.Date)] = note.Text;

        var weeks = new List<WeekSpan>();
        foreach (var day in days)
            if (weeks.Count == 0 || day.DayOfWeek == DayOfWeek.Monday) weeks.Add(new WeekSpan(day, day));
            else weeks[^1] = weeks[^1] with { To = day };
        var weekOf = days.Select((_, i) => weeks.FindIndex(w => days[i] >= w.From && days[i] <= w.To)).ToList();

        var today = BusinessDateService.Current();
        var rows = new List<AttendanceRow>(employees.Count);
        foreach (var employee in employees)
        {
            var standardHours = employee.Pregnant ? PregnantModel.StandardHours : IndexModel.StandardHours;
            var entryShift = employee.Pregnant ? PregnantModel.ShiftKey : employee.Shift;
            var cells = new List<string>(days.Count);
            decimal worked = 0, leave = 0, scheduled = 0, absent = 0;
            var notes = new List<string>();
            var dayNotes = new List<string>(days.Count);
            var dayLeaves = new List<LeaveType>(days.Count);
            var dayEmployed = new List<bool>(days.Count);
            var weekHours = new decimal[weeks.Count];
            foreach (var day in days)
            {
                if (remarks.TryGetValue((employee.Id, day), out var text) && text != "") { notes.Add($"{day:dd/MM}: {text}"); dayNotes.Add(text); }
                else dayNotes.Add("");
                if (day < employee.StartDate) { cells.Add(""); dayLeaves.Add(LeaveType.None); dayEmployed.Add(false); continue; }
                if (employee.ResignedDate is DateOnly last && day > last) { cells.Add(Resigned); dayLeaves.Add(LeaveType.None); dayEmployed.Add(false); continue; }

                var isOffDay = employee.Pregnant ? !EffectiveAttendance.IsPregnantWorkingDay(day) : !EffectiveAttendance.IsScheduled(calendar, employee.CurrentWorkshopId, employee.Shift, day);
                var cell = new IndexModel.AttendanceCell { BusinessDate = day, WorkshopId = employee.CurrentWorkshopId, WorkingHours = isOffDay ? 0 : standardHours, IsOffDay = isOffDay, IsConfirmed = true };
                if (staged.TryGetValue((employee.Id, day, entryShift), out var change))
                {
                    if (!change.Revert)
                    {
                        cell.WorkingHours = change.Hours;
                        cell.LeaveType = isOffDay ? LeaveType.None : (LeaveType)change.LeaveType;
                        cell.LeaveHours = isOffDay ? 0 : change.LeaveHours;
                    }
                }
                else if (entries.TryGetValue((employee.Id, day, entryShift), out var entry))
                {
                    cell.WorkingHours = entry.WorkingHours;
                    cell.LeaveType = isOffDay ? LeaveType.None : entry.LeaveType;
                    cell.LeaveHours = isOffDay ? 0 : entry.LeaveHours;
                }
                // A day that has not come yet shows no hours, whatever they are; leave, OFF and remarks stay.
                dayLeaves.Add(cell.LeaveType);
                dayEmployed.Add(true);
                if (day > today && !isOffDay && cell.LeaveType == LeaveType.None)
                {
                    cells.Add("");
                    scheduled += standardHours;
                    continue;
                }
                cells.Add(IndexModel.CellCode(cell));
                worked += cell.WorkingHours;
                weekHours[weekOf[day.Day - 1]] += cell.WorkingHours;
                if (cell.LeaveType != LeaveType.None) leave += cell.LeaveHours;
                if (cell.LeaveType == LeaveType.Absent) absent += cell.LeaveHours;
                if (!isOffDay) scheduled += standardHours;
            }
            rows.Add(new AttendanceRow(employee.EmployeeId, employee.FullName, employee.Workshop, employee.Process, employee.Shift, cells, worked, leave, string.Join("; ", notes), scheduled, absent, dayNotes, [.. weekHours], dayLeaves, dayEmployed));
        }
        return new AttendanceTable(monthStart, days, rows, weeks);
    }

    // Per day, workshop and shift: manpower and how many people took each leave type. Built from the same cells as the monthly
    // attendance report, so changes still waiting in the staging file count.
    public async Task<LeaveTrend> LeaveTrendAsync(string? month, int? workshopId, string? shift, CancellationToken cancellationToken = default, IReadOnlyCollection<int>? workshopScope = null)
    {
        var table = await AttendanceMonthAsync(month, workshopId, shift, cancellationToken, workshopScope, includePregnant: true);
        var today = BusinessDateService.Current();
        var rows = new List<LeaveDayRow>();
        foreach (var group in table.Rows.Where(r => ReportShifts.Contains(r.Shift)).GroupBy(r => (r.Workshop, r.Shift)).OrderBy(g => g.Key.Workshop, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key.Shift))
            for (var i = 0; i < table.Days.Count && table.Days[i] <= today; i++)
            {
                var manpower = group.Count(r => r.DayEmployed![i]);
                if (manpower == 0) continue;
                var leaves = LeaveTypes.ToDictionary(t => t, t => group.Count(r => r.DayLeaves![i] == t));
                rows.Add(new LeaveDayRow(table.Days[i], group.Key.Workshop, group.Key.Shift, manpower, leaves));
            }
        return new LeaveTrend(table.MonthStart, table.Days, [.. rows.OrderBy(r => r.Date).ThenBy(r => r.Workshop, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Shift)]);
    }

    // Closed business days only: a day without a summary has no hours yet.
    public async Task<List<HoursRow>> WorkingHoursAsync(DateOnly from, DateOnly to, int? workshopId, string? shift, CancellationToken cancellationToken = default)
    {
        var query = db.ShiftManpowers.AsNoTracking().Where(m => ReportShifts.Contains(m.Shift) && m.TotalHours != null && m.BusinessDate >= from && m.BusinessDate <= to);
        if (workshopId is int w) query = query.Where(m => m.WorkshopId == w);
        if (!string.IsNullOrEmpty(shift)) query = query.Where(m => m.Shift == shift);
        return (await query.OrderBy(m => m.BusinessDate).ThenBy(m => m.Workshop!.Name).ThenBy(m => m.Shift)
            .Select(m => new { m.BusinessDate, Workshop = m.Workshop!.Name, m.Shift, m.TotalHours, m.IndirectHours, m.DirectCount, m.IndirectCount })
            .ToListAsync(cancellationToken))
            .Select(m => new HoursRow(m.BusinessDate, m.Workshop, m.Shift, m.TotalHours ?? 0m, m.IndirectHours ?? 0m, m.DirectCount, m.IndirectCount)).ToList();
    }

    // state: "Active", "Resigned" or empty for everyone.
    public async Task<List<EmployeeRow>> EmployeesAsync(int? workshopId, string? shift, string? state, CancellationToken cancellationToken = default)
    {
        var query = db.Employees.AsNoTracking();
        if (workshopId is int w) query = query.Where(e => e.CurrentWorkshopId == w);
        if (!string.IsNullOrEmpty(shift)) query = query.Where(e => e.Shift == shift);
        if (Enum.TryParse<EmploymentState>(state, out var parsed)) query = query.Where(e => e.EmploymentState == parsed);
        return (await query.Select(e => new
            {
                e.EmployeeId, e.FullName, e.JobGrade, e.Process, Workshop = e.CurrentWorkshop!.Name, e.Shift, e.EmploymentState,
                e.StartDate, e.ResignedDate, e.IsNewEmployee, Groups = e.SpecialGroups.Select(g => g.SpecialGroup!.Name).ToList()
            }).ToListAsync(cancellationToken))
            .OrderBy(e => e.EmployeeId, StringComparer.OrdinalIgnoreCase)
            .Select(e => new EmployeeRow(e.EmployeeId, e.FullName, e.JobGrade, e.Process, e.Workshop, e.Shift, e.EmploymentState.ToString(),
                e.StartDate, e.ResignedDate, e.IsNewEmployee, string.Join("; ", e.Groups.Order()))).ToList();
    }
}
