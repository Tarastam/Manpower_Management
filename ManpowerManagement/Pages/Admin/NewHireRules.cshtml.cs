using ManpowerManagement.Data;
using ManpowerManagement.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Pages.Admin;

// The thresholds that flag a new hire as "at risk" and when the probation decision warning starts.
public class NewHireRulesModel(AppDbContext db) : PageModel
{
    [BindProperty] public NewHireRiskSetting Rules { get; set; } = new();
    [TempData] public string? Message { get; set; }
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        Rules = await db.NewHireRiskSettings.AsNoTracking().FirstOrDefaultAsync() ?? new NewHireRiskSetting();
        // A value saved under the old day-60 deadline would fail the form's own validation.
        Rules.DecisionWarningDay = Math.Min(Rules.DecisionWarningDay, Services.NewHireMonitorService.DecisionDay - 1);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var r = Rules;
        if (r.ConsecutiveAbsentDays is < 1 or > 30 || r.NoShowDays is < 1 or > 30 || r.LeaveRateWindowDays is < 3 or > 60)
            return Fail("Days must be between 1 and 30 (leave window between 3 and 60).");
        if (r.LeaveRatePercent is <= 0 or > 100) return Fail("Percentages must be between 0 and 100.");
        if (r.DecisionWarningDay is < 1 or >= Services.NewHireMonitorService.DecisionDay) return Fail($"The decision warning day must be before day {Services.NewHireMonitorService.DecisionDay}.");
        var setting = await db.NewHireRiskSettings.FirstOrDefaultAsync();
        if (setting is null) { setting = new NewHireRiskSetting(); db.NewHireRiskSettings.Add(setting); }
        setting.ConsecutiveAbsentDays = r.ConsecutiveAbsentDays; setting.NoShowDays = r.NoShowDays;
        setting.LeaveRatePercent = r.LeaveRatePercent; setting.LeaveRateWindowDays = r.LeaveRateWindowDays; setting.DecisionWarningDay = r.DecisionWarningDay;
        db.AuditLogs.Add(new AuditLog { EntityName = "NewHireRiskSetting", EntityKey = "1", Action = "Saved rules", Actor = User.Identity?.Name ?? "Admin" });
        await db.SaveChangesAsync();
        Message = "Early-warning rules saved.";
        return RedirectToPage();
    }

    // Keep what was typed so nothing has to be re-entered.
    IActionResult Fail(string error)
    {
        Error = error;
        return Page();
    }
}
