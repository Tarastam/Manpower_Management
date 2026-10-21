using ClosedXML.Excel;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ManpowerManagement.Pages;

public class IndexModel(WorkforceDashboardService dashboard, ReportService reports) : PageModel
{
    // Same figure as a card on the Absent Ratio report: average of the daily share of A, B and C shift manpower over the chosen period.
    public record LeaveRatio(string Code, string Name, decimal Ratio, bool IsTotal = false);

    public WorkforceDashboardService.Report Report { get; private set; } = new();
    public List<LeaveRatio> LeaveRatios { get; private set; } = [];

    // Shown in the date pickers: the days the figures cover (a month-to-date when nothing was picked).
    public DateOnly From => Report.PeriodStart;
    public DateOnly To => Report.PeriodEnd > Report.PeriodStart ? Report.PeriodEnd.AddDays(-1) : Report.PeriodStart;

    public async Task OnGetAsync(string? month, int? workshopId, string? period, DateOnly? from, DateOnly? to)
    {
        Report = await dashboard.LoadAsync(month, workshopId, period, from, to);
        var scope = Report.WorkshopId is int id ? WorkforceDashboardService.DescendantIds(Report.Workshops, id) : null;
        // The leave trend is built one month at a time; join the months the range touches.
        var trend = new ReportService.LeaveTrend(default, [], []);
        for (var m = new DateOnly(From.Year, From.Month, 1); m <= To; m = m.AddMonths(1))
        {
            var part = await reports.LeaveTrendAsync(m.ToString("yyyy-MM"), null, null, workshopScope: scope);
            trend = trend with { Rows = [.. trend.Rows, .. part.Rows] };
        }
        trend = trend.Between(Report.PeriodStart, Report.PeriodEnd.AddDays(-1));
        LeaveRatios =
        [
            new("Total", "Total leave", trend.Average(r => r.Total), true),
            new("Total", "Total leave (excl. AL)", trend.Average(r => r.TotalWithoutAl), true),
            .. ReportService.LeaveTypes.Select(t => new LeaveRatio(Attendance.IndexModel.LeaveCodes[t], LeaveName(t), trend.Average(r => r.Of(t))))
        ];
    }

    static string LeaveName(LeaveType type) => type switch
    {
        LeaveType.AnnualLeave => "Annual leave (AL)",
        LeaveType.SickLeave => "Sick leave (SL)",
        LeaveType.BusinessLeave => "Business leave (BL)",
        LeaveType.OtherLeave => "Other leave (OL)",
        LeaveType.Absent => "Absent (AB)",
        _ => "Mother leave (ML)"
    };

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
