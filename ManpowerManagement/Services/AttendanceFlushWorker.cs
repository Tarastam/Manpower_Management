using ManpowerManagement.Data;
using ManpowerManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Services;

// Copies the staged attendance changes to SQL Server at 07:00 and 19:00, then closes the new business day.
public class AttendanceFlushService(AppDbContext db, DailyHoursService dailyHours, AttendanceStagingStore store, ILogger<AttendanceFlushService> logger)
{
    public static readonly TimeSpan MorningSlot = new(7, 0, 0);
    public static readonly TimeSpan EveningSlot = new(19, 0, 0);
    // Business days older than this that were never summarized are not rebuilt by a flush.
    const int BackfillDays = 7;
    const int ReopenDays = 60;

    // The most recent 07:00 / 19:00 that has already passed.
    public static DateTime LatestSlot(DateTime now)
    {
        var evening = now.Date + EveningSlot; var morning = now.Date + MorningSlot;
        return now >= evening ? evening : now >= morning ? morning : now.Date.AddDays(-1) + EveningSlot;
    }

    // Runs the flush when a slot has passed since the last one (also catches up after the app was down).
    public async Task<bool> RunIfDueAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var slot = LatestSlot(now);
        if (store.LastSlot is DateTime last && last >= slot) return false;
        var touched = await FlushAsync(cancellationToken);
        var current = BusinessDateService.Current(now);
        await RefreshTouchedAsync(touched, current, cancellationToken);
        var finalized = await FinalizeClosedDaysAsync(current, cancellationToken);
        store.MarkSlot(slot, finalized);
        return true;
    }

    // Writes every staged item to SQL in one SaveChanges, then drops the flushed items from the staging file.
    // If SQL refuses, nothing is removed and the next run tries again.
    public async Task<HashSet<DateOnly>> FlushAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = store.Snapshot();
        var touched = new HashSet<DateOnly>();
        if (snapshot.IsEmpty) return touched;

        var changedEntries = 0;
        if (snapshot.Cells.Count > 0)
        {
            var ids = snapshot.Cells.Select(c => c.EmployeeId).Distinct().ToList();
            var min = snapshot.Cells.Min(c => c.Date); var max = snapshot.Cells.Max(c => c.Date);
            var validEmployees = (await db.Employees.Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(cancellationToken)).ToHashSet();
            var validWorkshops = (await db.Workshops.Select(w => w.Id).ToListAsync(cancellationToken)).ToHashSet();
            var existing = (await db.AttendanceEntries.Where(e => e.BusinessDate >= min && e.BusinessDate <= max && ids.Contains(e.EmployeeId)).ToListAsync(cancellationToken))
                .ToDictionary(e => (e.EmployeeId, e.BusinessDate, e.Shift));
            foreach (var change in snapshot.Cells.OrderBy(c => c.Version))
            {
                if (!validEmployees.Contains(change.EmployeeId)) { logger.LogWarning("Staged attendance for unknown employee {Employee} was dropped.", change.EmployeeId); continue; }
                existing.TryGetValue((change.EmployeeId, change.Date, change.Shift), out var entry);
                touched.Add(change.Date);
                if (change.Revert) { if (entry is not null) { db.AttendanceEntries.Remove(entry); existing.Remove((change.EmployeeId, change.Date, change.Shift)); changedEntries++; } continue; }
                if (!validWorkshops.Contains(change.WorkshopId)) { logger.LogWarning("Staged attendance for unknown workshop {Workshop} was dropped.", change.WorkshopId); continue; }
                if (entry is null)
                {
                    entry = new AttendanceEntry { BusinessDate = change.Date, Shift = change.Shift, EmployeeId = change.EmployeeId };
                    db.AttendanceEntries.Add(entry); existing[(change.EmployeeId, change.Date, change.Shift)] = entry;
                }
                entry.WorkshopId = change.WorkshopId; entry.WorkingHours = change.Hours;
                entry.LeaveType = (LeaveType)change.LeaveType; entry.LeaveHours = change.LeaveType == 0 ? 0 : change.LeaveHours;
                entry.IsConfirmed = true; entry.EnteredBy = change.Actor; entry.UpdatedAtUtc = DateTime.UtcNow;
                changedEntries++;
            }
        }

        var changedManpower = 0;
        if (snapshot.Manpower.Count > 0)
        {
            var min = snapshot.Manpower.Min(m => m.Date); var max = snapshot.Manpower.Max(m => m.Date);
            var rows = (await db.ShiftManpowers.Where(m => m.BusinessDate >= min && m.BusinessDate <= max).ToListAsync(cancellationToken))
                .ToDictionary(m => (m.BusinessDate, m.WorkshopId, m.Shift));
            var validWorkshops = (await db.Workshops.Select(w => w.Id).ToListAsync(cancellationToken)).ToHashSet();
            foreach (var change in snapshot.Manpower.OrderBy(m => m.Version))
            {
                if (!validWorkshops.Contains(change.WorkshopId)) continue;
                if (!rows.TryGetValue((change.Date, change.WorkshopId, change.Shift), out var row))
                {
                    row = new ShiftManpower { BusinessDate = change.Date, WorkshopId = change.WorkshopId, Shift = change.Shift };
                    db.ShiftManpowers.Add(row); rows[(change.Date, change.WorkshopId, change.Shift)] = row;
                }
                row.Indirect1067Count = change.I1067; row.Indirect841Count = change.I841; row.Indirect733Count = change.I733;
                row.IndirectCount = change.I1067 + change.I841 + change.I733;
                row.EnteredBy = change.Actor; row.UpdatedAtUtc = DateTime.UtcNow;
                touched.Add(change.Date); changedManpower++;
            }
        }

        db.AuditLogs.Add(new AuditLog { EntityName = "Attendance", EntityKey = $"flush:{DateTime.Now:yyyy-MM-dd HH:mm}", Action = "Flushed staged attendance", Actor = "system",
            Detail = $"{changedEntries} attendance changes, {changedManpower} indirect headcounts" });
        await db.SaveChangesAsync(cancellationToken);
        store.Remove(snapshot);
        return touched;
    }

    // Counts again every day a flush touched, so the daily hours always follow what was keyed.
    async Task RefreshTouchedAsync(HashSet<DateOnly> touched, DateOnly current, CancellationToken cancellationToken)
    {
        if (touched.Count == 0) return;
        var closed = touched.Where(d => d < current).ToList();
        foreach (var day in closed.Where(d => d >= current.AddDays(-ReopenDays)).Order())
            await dailyHours.FinalizeDayAsync(day, cancellationToken);
        var others = touched.Except(closed.Where(d => d >= current.AddDays(-ReopenDays))).ToHashSet();
        await dailyHours.RefreshAsync(others, cancellationToken);
    }

    // New day: every business day that has closed since the last run gets its summary, even if nobody keyed anything.
    async Task<DateOnly?> FinalizeClosedDaysAsync(DateOnly current, CancellationToken cancellationToken)
    {
        var last = current.AddDays(-1);
        var start = store.LastFinalizedDay is DateOnly done ? done.AddDays(1) : last;
        if (start < current.AddDays(-BackfillDays)) start = current.AddDays(-BackfillDays);
        for (var day = start; day <= last; day = day.AddDays(1))
            await dailyHours.FinalizeDayAsync(day, cancellationToken);
        // Rows from before this model that never got their totals.
        var pending = (await db.ShiftManpowers.AsNoTracking().Where(m => m.BusinessDate < current && m.TotalHours == null)
            .Select(m => m.BusinessDate).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        await dailyHours.RefreshAsync(pending, cancellationToken);
        return start <= last ? last : null;
    }
}

public class AttendanceFlushWorker(IServiceScopeFactory scopes, ILogger<AttendanceFlushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var ran = await scope.ServiceProvider.GetRequiredService<AttendanceFlushService>().RunIfDueAsync(DateTime.Now, stoppingToken);
                if (ran) logger.LogInformation("Attendance flushed to the database.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Attendance flush failed; it will be retried."); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
