using System.Text.Json;
using ManpowerManagement.Data;
using ManpowerManagement.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Services;

// The workshop the leader chose at check-in, re-read from the Admin registration on every request.
public record LeaderSession(int AssignmentId, int EmployeeKey, string EmployeeCode, string Name, int WorkshopId, string WorkshopName)
{
    public string Actor => $"leader:{EmployeeCode} {Name}";
}

// Without user accounts, a shift leader checks in with their own employee ID. The encrypted cookie only
// carries that ID, the chosen assignment and the business date; the workshop always comes from the Admin leader registration, so the
// attendance pages cannot be pointed at another team by editing the URL or the form.
public class LeaderSessionService(IDataProtectionProvider dataProtection)
{
    public const string CookieName = "mp.attendance.leader";
    readonly IDataProtector protector = dataProtection.CreateProtector("Attendance.LeaderSession.v1");
    record Payload(string EmployeeCode, int AssignmentId, DateOnly BusinessDate);

    public async Task<LeaderSession?> ReadAsync(HttpContext context, AppDbContext db)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var raw) || string.IsNullOrEmpty(raw)) return null;
        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(raw)); }
        catch { return null; }
        // A check-in is valid for one business day only, so the next shift must check in again.
        if (payload is null || payload.BusinessDate != BusinessDateService.Current()) return null;
        return (await FindAsync(db, payload.EmployeeCode)).FirstOrDefault(s => s.AssignmentId == payload.AssignmentId);
    }

    // Only employee IDs registered by Admin on Admin/Leaders can check in. A leader may have several
    // workshop assignments and picks one at check-in; the shift is chosen on the attendance page.
    public const string DayShift = "Day";

    public static async Task<List<LeaderSession>> FindAsync(AppDbContext db, string employeeCode)
    {
        var code = employeeCode.Trim();
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.EmployeeId == code && e.EmploymentState == EmploymentState.Active);
        var assignments = await db.AttendanceLeaders.Where(l => l.IsActive && l.EmployeeCode == code).OrderBy(l => l.Workshop!.Name).ThenBy(l => l.Id)
            .Select(l => new { l.Id, l.WorkshopId, Workshop = l.Workshop!.Name, l.LeaderName }).ToListAsync();
        // Leaders outside the employee master are registered with a name.
        return assignments.DistinctBy(a => a.WorkshopId)
            .Select(a => new LeaderSession(a.Id, employee?.Id ?? 0, code, employee?.FullName ?? a.LeaderName ?? code, a.WorkshopId, a.Workshop)).ToList();
    }

    public void Write(HttpContext context, LeaderSession session)
    {
        var businessDate = BusinessDateService.Current();
        var value = protector.Protect(JsonSerializer.Serialize(new Payload(session.EmployeeCode, session.AssignmentId, businessDate)));
        // The business day rolls over at 07:00 (see BusinessDateService).
        var expires = businessDate.AddDays(1).ToDateTime(new TimeOnly(7, 0));
        context.Response.Cookies.Append(CookieName, value, new CookieOptions { HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Strict, Expires = new DateTimeOffset(expires) });
    }

    public void Clear(HttpContext context) => context.Response.Cookies.Delete(CookieName);
}
