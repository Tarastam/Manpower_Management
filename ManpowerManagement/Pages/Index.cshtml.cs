using ClosedXML.Excel;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ManpowerManagement.Pages;

public class IndexModel(WorkforceDashboardService dashboard) : PageModel
{
    public WorkforceDashboardService.Report Report { get; private set; } = new();

    public async Task OnGetAsync(string? month, int? workshopId) =>
        Report = await dashboard.LoadAsync(month, workshopId);

    // Keep the existing download endpoint for integrations; the dashboard has no export button.
    public async Task<IActionResult> OnGetExportAsync(string? month, int? workshopId)
    {
        var report = await dashboard.LoadAsync(month, workshopId);
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Dashboard");
        sheet.Cell(1, 1).Value = "Metric";
        sheet.Cell(1, 2).Value = "Value";
        var metrics = new (string Name, string Value)[]
        {
            ("Active headcount", report.Headcount.ToString("N0")),
            ("Present person-days", report.PresentPersonDays.ToString("N0")),
            ("Scheduled person-days", report.ScheduledPersonDays.ToString("N0")),
            ("Attendance rate (headcount)", report.AttendanceRate.ToString("N2") + "%"),
            ("Absence rate", report.AbsenceRate.ToString("N2") + "%"),
            ("Turnover rate", report.TurnoverRate.ToString("N2") + "%")
        };
        for (var i = 0; i < metrics.Length; i++)
        {
            sheet.Cell(i + 2, 1).Value = metrics[i].Name;
            sheet.Cell(i + 2, 2).Value = metrics[i].Value;
        }
        sheet.Cell(metrics.Length + 3, 1).InsertTable(report.LeaveRows.Select(x => new
        {
            LeaveType = x.Name, x.Hours, SharePercent = x.Rate
        }));
        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return File(stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"manpower-{report.Month}.xlsx");
    }
}
