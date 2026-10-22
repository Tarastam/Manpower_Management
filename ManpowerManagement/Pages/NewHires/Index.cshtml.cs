using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ManpowerManagement.Pages.NewHires;

public class IndexModel(NewHireMonitorService monitor) : PageModel
{
    public static readonly (string Value, string Label)[] PhaseFilters =
    [
        ("all", "All new hires"), ("attention", "Decision due or overdue"), ("risk", "At risk"),
        ("evaluating", "Evaluating"), ("extended", "Extended probation"), ("decided", "Decided")
    ];

    [BindProperty(SupportsGet = true)] public int? WorkshopId { get; set; }
    [BindProperty(SupportsGet = true)] public string Phase { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [TempData] public string? Message { get; set; }
    public NewHireAccess Access { get; private set; } = new();
    public NewHireMonitorService.Monitor Data { get; private set; } = new();
    public List<NewHireMonitorService.NewHireRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        Access = await monitor.ResolveAccessAsync(HttpContext);
        if (!Access.CanView) return RedirectToPage("/Attendance/CheckIn", new { returnPage = "NewHires" });
        Data = await monitor.LoadAsync(Access, WorkshopId);
        Rows = Data.Rows.Where(Matches).ToList();
        return Page();
    }

    bool Matches(NewHireMonitorService.NewHireRow row) => MatchesSearch(row) && Phase switch
    {
        "attention" => row.Phase is NewHirePhase.DecisionSoon or NewHirePhase.DecisionOverdue,
        "risk" => row.AtRisk,
        "evaluating" => row.Phase is NewHirePhase.Evaluating or NewHirePhase.NotStarted,
        "extended" => row.Phase == NewHirePhase.Extended,
        "decided" => row.Phase is NewHirePhase.Passed or NewHirePhase.NotPassed,
        _ => true
    };

    bool MatchesSearch(NewHireMonitorService.NewHireRow row) =>
        string.IsNullOrWhiteSpace(Q) || row.Code.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase) || row.Name.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase);
}
