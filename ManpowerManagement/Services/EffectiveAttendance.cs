using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Pages.Attendance;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Services;

// What an employee counts for on one business day. Hours are the keyed entry's working hours when there is one,
// otherwise the standard hours of a working day.
public record EffectiveDay(int EmployeeId, DateOnly Date, int WorkshopId, string Shift, decimal Hours, LeaveType Leave, decimal LeaveHours);

// Single source of truth for "everyone counts 100% unless a change was keyed".
// Only exceptions are stored in AttendanceEntries, so a working day with no entry means the standard hours.
public class EffectiveAttendance(AppDbContext db)
{
    public static bool IsScheduled(IReadOnlyDictionary<(int WorkshopId, DateOnly Date), bool> calendar, int workshopId, string shift, DateOnly day) =>
        calendar.TryGetValue((workshopId, day), out var working) ? working : !IndexModel.IsDefaultOffDay(shift, day);

    // Pregnant employees follow Monday to Friday, like their own page.
    public static bool IsPregnantWorkingDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    record Person(int Id, int WorkshopId, string Shift, DateOnly StartDate, DateOnly? ResignedDate, bool Pregnant, bool InRoster);

    // Effective attendance for every employed person on each day of [from, to] (limited to onlyDates when given).
    public async Task<List<EffectiveDay>> LoadAsync(DateOnly from, DateOnly to, IReadOnlySet<DateOnly>? onlyDates = null, CancellationToken cancellationToken = default)
    {
        var people = (await db.Employees.AsNoTracking()
            .Where(e => e.StartDate <= to && (e.ResignedDate == null ? e.EmploymentState == EmploymentState.Active : e.ResignedDate >= from))
            .Select(e => new { e.Id, e.CurrentWorkshopId, e.Shift, e.StartDate, e.ResignedDate, Pregnant = e.SpecialGroups.Any(g => g.SpecialGroup!.Name == PregnantModel.GroupName) })
            .ToListAsync(cancellationToken))
            .ToDictionary(e => e.Id, e => new Person(e.Id, e.CurrentWorkshopId, e.Shift, e.StartDate, e.ResignedDate, e.Pregnant, true));

        var entries = await db.AttendanceEntries.AsNoTracking()
            .Where(e => e.BusinessDate >= from && e.BusinessDate <= to && e.IsConfirmed)
            .Select(e => new { e.EmployeeId, e.BusinessDate, e.Shift, e.WorkshopId, e.WorkingHours, e.LeaveType, e.LeaveHours })
            .ToListAsync(cancellationToken);
        if (onlyDates is not null) entries = entries.Where(e => onlyDates.Contains(e.BusinessDate)).ToList();

        // People with a keyed entry who are no longer on the roster (e.g. resigned without a date) still count for the days they worked.
        var missing = entries.Select(e => e.EmployeeId).Distinct().Where(id => !people.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            foreach (var e in await db.Employees.AsNoTracking().Where(e => missing.Contains(e.Id))
                .Select(e => new { e.Id, e.CurrentWorkshopId, e.Shift, e.StartDate, e.ResignedDate, Pregnant = e.SpecialGroups.Any(g => g.SpecialGroup!.Name == PregnantModel.GroupName) }).ToListAsync(cancellationToken))
                people[e.Id] = new Person(e.Id, e.CurrentWorkshopId, e.Shift, e.StartDate, e.ResignedDate, e.Pregnant, false);

        var calendar = (await db.WorkCalendars.AsNoTracking().Where(c => c.WorkDate >= from && c.WorkDate <= to).ToListAsync(cancellationToken))
            .ToDictionary(c => (c.WorkshopId, c.WorkDate), c => c.IsWorkingDay);

        // An employee normally has one entry per day; if a shift change left two, the one matching the current roster wins.
        var entryByDay = entries.GroupBy(e => (e.EmployeeId, e.BusinessDate)).ToDictionary(g => g.Key, g =>
        {
            var person = people[g.Key.EmployeeId];
            var wanted = person.Pregnant ? PregnantModel.ShiftKey : person.Shift;
            return g.FirstOrDefault(e => e.Shift == wanted) ?? g.First();
        });

        var days = new List<DateOnly>();
        for (var day = from; day <= to; day = day.AddDays(1)) if (onlyDates is null || onlyDates.Contains(day)) days.Add(day);

        var result = new List<EffectiveDay>();
        foreach (var person in people.Values)
            foreach (var day in days)
            {
                if (entryByDay.TryGetValue((person.Id, day), out var entry))
                {
                    var shift = entry.Shift == PregnantModel.ShiftKey ? person.Shift : entry.Shift;
                    result.Add(new EffectiveDay(person.Id, day, entry.WorkshopId, shift, entry.WorkingHours, entry.LeaveType, entry.LeaveHours));
                    continue;
                }
                if (!person.InRoster || day < person.StartDate || (person.ResignedDate is DateOnly last && day > last)) continue;
                if (person.Pregnant ? !IsPregnantWorkingDay(day) : !IsScheduled(calendar, person.WorkshopId, person.Shift, day)) continue;
                result.Add(new EffectiveDay(person.Id, day, person.WorkshopId, person.Shift, person.Pregnant ? PregnantModel.StandardHours : IndexModel.StandardHours, LeaveType.None, 0));
            }
        return result;
    }
}
