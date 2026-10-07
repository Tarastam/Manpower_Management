using ManpowerManagement.Data;
using ManpowerManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Services;

// An approved resignation stores the employee's last working day. The day after it, this flips the employee to Resigned
// (at start-up, so downtime is caught up, and then every 10 minutes).
public class ResignationWorker(IServiceScopeFactory scopes, ILogger<ResignationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var today = DateOnly.FromDateTime(DateTime.Today);
                var due = await db.Employees
                    .Where(e => e.EmploymentState == EmploymentState.Active && e.ResignedDate != null && e.ResignedDate < today)
                    .ToListAsync(stoppingToken);
                if (due.Count > 0)
                {
                    foreach (var employee in due)
                    {
                        employee.EmploymentState = EmploymentState.Resigned;
                        db.AuditLogs.Add(new AuditLog { EntityName = "Employee", EntityKey = employee.EmployeeId, Action = "Resigned", Actor = "System" });
                    }
                    await db.SaveChangesAsync(stoppingToken);
                    logger.LogInformation("Marked {Count} employee(s) as Resigned.", due.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Resignation status update failed; it will be retried."); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
