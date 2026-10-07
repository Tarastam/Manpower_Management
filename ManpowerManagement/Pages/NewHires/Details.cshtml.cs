using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Pages.NewHires;

public class DetailsModel(AppDbContext db, NewHireMonitorService monitor) : PageModel
{
    [BindProperty] public ProbationDecision Decision { get; set; }
    [BindProperty] public DateOnly? ExtendedUntil { get; set; }
    [BindProperty] public string? Comment { get; set; }
    [TempData] public string? Message { get; set; }
    public string? Error { get; set; }
    public NewHireAccess Access { get; private set; } = new();
    public NewHireMonitorService.Detail? Data { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (await LoadAsync(id) is { } redirect) return redirect;
        return Data is null ? NotFound() : Page();
    }

    // The leader (or Admin) decides; HR only acknowledges afterwards.
    public async Task<IActionResult> OnPostDecisionAsync(int id)
    {
        if (await LoadAsync(id) is { } redirect) return redirect;
        if (Data is null) return NotFound();
        if (!Data.DecisionOpen) return await Fail("A decision has already been recorded for this employee.");
        if (!Data.CanDecide) return await Fail("Only Admin, or a leader checked in for this employee's line, can record the probation decision.");
        var comment = (Comment ?? "").Trim();
        if (comment.Length > 500) return await Fail("The comment can be at most 500 characters.");
        if (Decision != ProbationDecision.Passed && comment.Length == 0) return await Fail("Add a comment explaining an extension or a rejection.");
        if (!Enum.IsDefined(Decision)) return await Fail("Choose a decision.");
        if (Decision == ProbationDecision.Extended)
        {
            if (Data.ExtendFrom is null) return await Fail("Probation cannot be extended any further (119 days is the maximum).");
            if (ExtendedUntil is not DateOnly until || until < Data.ExtendFrom || until > Data.ExtendTo)
                return await Fail($"Choose an end date between {NewHireText.Date(Data.ExtendFrom.Value)} and {NewHireText.Date(Data.ExtendTo!.Value)}.");
        }

        var employee = await db.Employees.FirstAsync(e => e.Id == id);
        db.ProbationReviews.Add(new ProbationReview
        {
            EmployeeId = id, Decision = Decision, ExtendedUntil = Decision == ProbationDecision.Extended ? ExtendedUntil : null,
            LeaderComment = comment, DecidedBy = Access.Actor, DecidedAtUtc = DateTime.UtcNow
        });
        var note = "";
        // A rejection goes through the normal resignation ticket so an approver sets the real resignation date.
        if (Decision == ProbationDecision.NotPassed && !await db.ResignationTickets.AnyAsync(t => t.EmployeeId == id && t.Status == TicketStatus.Submitted))
        {
            db.ResignationTickets.Add(new ResignationTicket
            {
                RequesterEmployeeId = Access.Leader?.EmployeeCode ?? Access.Actor, RequesterShift = employee.Shift,
                WorkshopId = employee.CurrentWorkshopId, EmployeeId = id
            });
            note = " A resignation ticket was submitted for approval.";
        }
        db.AuditLogs.Add(new AuditLog { EntityName = "ProbationReview", EntityKey = id.ToString(), Action = Decision.ToString(), Actor = Access.Actor, Detail = comment });
        await db.SaveChangesAsync();
        Message = $"{employee.FullName}: {NewHireText.DecisionLabel(Decision).ToLowerInvariant()} recorded.{note}";
        return RedirectToPage(new { id });
    }

    // The leader's comment on one leave day marks it as considered for the probation decision.
    public async Task<IActionResult> OnPostLeaveNoteAsync(int id, DateOnly date, string? note)
    {
        if (await LoadAsync(id) is { } redirect) return redirect;
        if (Data is null) return NotFound();
        if (!Data.CanNote) return await Fail("Only Admin, or a leader checked in for this employee's line, can comment.");
        if (Data.Leaves.All(l => l.Date != date)) return await Fail("There is no leave on that day.");
        var text = (note ?? "").Trim();
        if (text.Length == 0) return await Fail("Add a comment on how this leave affects the probation decision.");
        if (text.Length > 500) return await Fail("The comment can be at most 500 characters.");
        await SaveNoteAsync(id, date, NewHireNoteKind.Leave, text, "Leave reviewed");
        Message = $"Leave on {NewHireText.Date(date)} reviewed.";
        return RedirectToPage(new { id });
    }

    // A comment that the employee came in late, one per day.
    public async Task<IActionResult> OnPostLateNoteAsync(int id, DateOnly date, string? note)
    {
        if (await LoadAsync(id) is { } redirect) return redirect;
        if (Data is null) return NotFound();
        if (!Data.CanNote) return await Fail("Only Admin, or a leader checked in for this employee's line, can comment.");
        var text = (note ?? "").Trim();
        if (text.Length == 0) return await Fail("Add a comment about the late arrival.");
        if (text.Length > 500) return await Fail("The comment can be at most 500 characters.");
        if (date < Data.Employee.StartDate || date > BusinessDateService.Current()) return await Fail("Choose a date between the start date and today.");
        await SaveNoteAsync(id, date, NewHireNoteKind.Late, text, "Late noted");
        Message = $"Late arrival on {NewHireText.Date(date)} saved.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteLateAsync(int id, int noteId)
    {
        if (await LoadAsync(id) is { } redirect) return redirect;
        if (Data is null) return NotFound();
        if (!Data.CanNote) return await Fail("Only Admin, or a leader checked in for this employee's line, can remove a comment.");
        var existing = await db.NewHireNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.EmployeeId == id && n.Kind == NewHireNoteKind.Late);
        if (existing is not null)
        {
            db.NewHireNotes.Remove(existing);
            db.AuditLogs.Add(new AuditLog { EntityName = "NewHireNote", EntityKey = id.ToString(), Action = "Late note removed", Actor = Access.Actor, Detail = $"{existing.BusinessDate:yyyy-MM-dd}: {existing.Comment}" });
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { id });
    }

    async Task SaveNoteAsync(int id, DateOnly date, string kind, string text, string action)
    {
        var existing = await db.NewHireNotes.FirstOrDefaultAsync(n => n.EmployeeId == id && n.Kind == kind && n.BusinessDate == date);
        if (existing is null) db.NewHireNotes.Add(new NewHireNote { EmployeeId = id, BusinessDate = date, Kind = kind, Comment = text, CreatedBy = Access.Actor });
        else { existing.Comment = text; existing.CreatedBy = Access.Actor; existing.CreatedAtUtc = DateTime.UtcNow; }
        db.AuditLogs.Add(new AuditLog { EntityName = "NewHireNote", EntityKey = id.ToString(), Action = action, Actor = Access.Actor, Detail = $"{date:yyyy-MM-dd}: {text}" });
        await db.SaveChangesAsync();
    }

    // HR confirms they have seen the decision. A pass also makes the employee permanent.
    public async Task<IActionResult> OnPostAcknowledgeAsync(int id, int reviewId)
    {
        if (await LoadAsync(id) is { } redirect) return redirect;
        if (Data is null) return NotFound();
        if (!Access.IsHr) return await Fail("Only HR can acknowledge a decision.");
        var review = await db.ProbationReviews.FirstOrDefaultAsync(r => r.Id == reviewId && r.EmployeeId == id);
        if (review is null || review.HrAcknowledgedAtUtc is not null) return RedirectToPage(new { id });
        if (Data.Reviews.FirstOrDefault()?.Id != review.Id) return await Fail("Only the latest decision can be acknowledged.");

        review.HrAcknowledgedBy = Access.Actor;
        review.HrAcknowledgedAtUtc = DateTime.UtcNow;
        var employee = await db.Employees.FirstAsync(e => e.Id == id);
        var made = "";
        if (review.Decision == ProbationDecision.Passed && employee.EmploymentType != EmploymentType.Permanent)
        {
            employee.EmploymentType = EmploymentType.Permanent;
            db.EmployeeHistories.Add(new EmployeeHistory
            {
                EmployeeId = id, WorkshopId = employee.CurrentWorkshopId, EmploymentType = EmploymentType.Permanent,
                EffectiveDate = BusinessDateService.Current(), ChangedBy = Access.Actor
            });
            made = " The employee is now permanent.";
        }
        db.AuditLogs.Add(new AuditLog { EntityName = "ProbationReview", EntityKey = id.ToString(), Action = "HR acknowledged", Actor = Access.Actor, Detail = review.Decision.ToString() });
        await db.SaveChangesAsync();
        Message = $"Decision acknowledged.{made}";
        return RedirectToPage(new { id });
    }

    async Task<IActionResult?> LoadAsync(int id)
    {
        Access = await monitor.ResolveAccessAsync(HttpContext);
        if (!Access.CanView) return RedirectToPage("/Attendance/CheckIn", new { returnPage = "NewHires" });
        Data = await monitor.LoadDetailAsync(Access, id);
        return null;
    }

    async Task<IActionResult> Fail(string error)
    {
        Error = error;
        // Nothing was saved, so show the stored state again.
        Data = await monitor.LoadDetailAsync(Access, Data!.Employee.Id);
        return Page();
    }
}
