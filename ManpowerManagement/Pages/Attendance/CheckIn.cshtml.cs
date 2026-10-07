using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ManpowerManagement.Pages.Attendance;

public class CheckInModel(AppDbContext db, LeaderSessionService leaderSessions) : PageModel
{
    [BindProperty] public string EmployeeCode { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnPage { get; set; }
    public LeaderSession? Current { get; set; }
    [BindProperty] public int AssignmentId { get; set; }
    public List<LeaderSession> Candidates { get; set; } = [];
    public string? Error { get; set; }
    public bool IsAdmin => User.IsInRole(Roles.Admin);

    public async Task OnGetAsync() => Current = await leaderSessions.ReadAsync(HttpContext, db);

    // Step 1: look the ID up and show whose workshops will be offered, before anything is stored.
    public async Task<IActionResult> OnPostLookupAsync()
    {
        if (string.IsNullOrWhiteSpace(EmployeeCode)) { Error = "Enter your employee ID."; return Page(); }
        Candidates = await LeaderSessionService.FindAsync(db, EmployeeCode);
        Current = await leaderSessions.ReadAsync(HttpContext, db);
        AssignmentId = Current is not null && Candidates.Any(c => c.AssignmentId == Current.AssignmentId) ? Current.AssignmentId : Candidates.FirstOrDefault()?.AssignmentId ?? 0;
        if (Candidates.Count == 0) Error = $"{EmployeeCode.Trim()} is not registered as an attendance leader. Ask Admin to add it on the Leaders page.";
        return Page();
    }

    // Step 2: the leader confirms the workshop shown on screen.
    public async Task<IActionResult> OnPostConfirmAsync()
    {
        var session = (await LeaderSessionService.FindAsync(db, EmployeeCode)).FirstOrDefault(c => c.AssignmentId == AssignmentId);
        if (session is null) { Error = "Employee ID is no longer valid. Please check in again."; return Page(); }
        leaderSessions.Write(HttpContext, session);
        db.AuditLogs.Add(new AuditLog { EntityName = "Attendance", EntityKey = $"{session.WorkshopId}", Action = "Leader check-in", Actor = session.Actor });
        await db.SaveChangesAsync();
        return RedirectToPage(ReturnPage switch { "Pregnant" => "/Attendance/Pregnant", "NewHires" => "/NewHires/Index", "Employees" => "/Employees/Index", _ => "/Attendance/Index" });
    }

    public IActionResult OnPostSignOut()
    {
        leaderSessions.Clear(HttpContext);
        return RedirectToPage();
    }
}
