using ManpowerManagement.Data;
using ManpowerManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Services;

public class DailyHoursService(AppDbContext db, EffectiveAttendance effective)
{
    // Rebuild the daily totals from effective attendance: everyone counts the standard hours on a working day,
    // reduced by what was keyed. A manual indirect correction is preserved.
    public Task RefreshAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        RefreshAsync(from, to, null, cancellationToken);

    public Task RefreshAsync(IReadOnlySet<DateOnly> dates, CancellationToken cancellationToken = default) =>
        dates.Count == 0 ? Task.CompletedTask : RefreshAsync(dates.Min(), dates.Max(), dates, cancellationToken);

    async Task RefreshAsync(DateOnly from, DateOnly to, IReadOnlySet<DateOnly>? onlyDates, CancellationToken cancellationToken)
    {
        var summaries = await db.ShiftManpowers.Where(m => m.BusinessDate >= from && m.BusinessDate <= to).ToListAsync(cancellationToken);
        if (onlyDates is not null) summaries = summaries.Where(m => onlyDates.Contains(m.BusinessDate)).ToList();
        if (summaries.Count == 0) return;
        var dates = summaries.Select(m => m.BusinessDate).ToHashSet();
        var days = await effective.LoadAsync(dates.Min(), dates.Max(), dates, cancellationToken);

        var leaders = (await db.Employees.AsNoTracking()
            .Where(e => db.AttendanceLeaders.Any(l => l.IsActive && l.EmployeeCode == e.EmployeeId))
            .Select(e => new { e.Id, e.JobGrade }).ToListAsync(cancellationToken))
            .ToDictionary(e => e.Id, e => e.JobGrade);
        // Leaders below 56T are direct employees and only half of their hours count.
        var halfCountLeaderIds = leaders.Where(l => IsBelow56T(l.Value)).Select(l => l.Key).ToHashSet();
        var groups = days.GroupBy(e => (e.Date, e.WorkshopId, e.Shift)).ToDictionary(g => g.Key, g => g.ToList());
        var currentDay = BusinessDateService.Current();

        foreach (var summary in summaries)
        {
            groups.TryGetValue((summary.BusinessDate, summary.WorkshopId, summary.Shift), out var group);
            group ??= [];
            // Headcount follows attendance: Direct = present - indirect, with every present leader counted as indirect.
            var present = group.Count(e => e.Hours > 0);
            var leadersPresent = group.Count(e => leaders.ContainsKey(e.EmployeeId) && e.Hours > 0);
            summary.IndirectCount = summary.Indirect1067Count + summary.Indirect841Count + summary.Indirect733Count + leadersPresent;
            summary.DirectCount = present - summary.IndirectCount;
            // A business day that has not closed yet has no hour summary.
            if (summary.BusinessDate >= currentDay) continue;

            // Day shift is entirely indirect, so the below-56T half count does not apply there.
            var isDay = summary.Shift == LeaderSessionService.DayShift;
            summary.TotalHours = group.Sum(e => !isDay && halfCountLeaderIds.Contains(e.EmployeeId) ? e.Hours * 0.5m : e.Hours);
            if (!summary.IndirectHoursIsManual)
                summary.IndirectHours = isDay
                    ? summary.TotalHours
                    : summary.Indirect1067Count * 10.67m
                    + summary.Indirect841Count * 8.41m
                    + summary.Indirect733Count * 7.33m
                    + group.Where(e => leaders.ContainsKey(e.EmployeeId) && !halfCountLeaderIds.Contains(e.EmployeeId)).Sum(e => e.Hours);
            summary.CalculatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    // Closes a business day: every workshop and shift that had anyone scheduled gets its summary row,
    // even when nobody keyed anything, then the hours are counted.
    public async Task FinalizeDayAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var days = await effective.LoadAsync(day, day, null, cancellationToken);
        var existing = (await db.ShiftManpowers.Where(m => m.BusinessDate == day).Select(m => new { m.WorkshopId, m.Shift }).ToListAsync(cancellationToken))
            .Select(m => (m.WorkshopId, m.Shift)).ToHashSet();
        foreach (var key in days.Select(d => (d.WorkshopId, d.Shift)).Distinct().Where(k => !existing.Contains(k)))
            db.ShiftManpowers.Add(new ShiftManpower { BusinessDate = day, WorkshopId = key.WorkshopId, Shift = key.Shift });
        await db.SaveChangesAsync(cancellationToken);
        await RefreshAsync(day, day, cancellationToken);
    }

    // Job grades look like "56T"; a grade with no leading number is not treated as below 56T.
    internal static bool IsBelow56T(string? jobGrade)
    {
        var digits = new string((jobGrade ?? "").Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var grade) && grade < 56;
    }
}
