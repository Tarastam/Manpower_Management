using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ManpowerManagement.Pages;

public class HighLeaveModel(WorkforceDashboardService dashboard) : PageModel
{
    public WorkforceDashboardService.Report Report { get; private set; } = new();
    public List<WorkforceDashboardService.HighLeaveEmployee> Employees { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string? month, int? workshopId, string? period)
    {
        if (workshopId is null) return RedirectToPage("/Index", new { month, period });
        Report = await dashboard.LoadAsync(month, workshopId, period);
        if (Report.WorkshopId is null) return NotFound();
        Employees = Report.HighLeaveEmployees
            .Where(x => x.WorkshopId == Report.WorkshopId)
            .OrderByDescending(x => x.Rate).ThenBy(x => x.Name).ToList();
        return Page();
    }
}
