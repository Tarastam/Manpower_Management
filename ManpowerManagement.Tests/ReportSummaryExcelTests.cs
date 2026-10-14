using ClosedXML.Excel;
using ManpowerManagement.Services;
using Xunit;

namespace ManpowerManagement.Tests;

public class ReportSummaryExcelTests
{
    static WorkforceDashboardService.Report SampleReport() => new()
    {
        Month = "2026-03",
        PeriodEnd = new DateOnly(2026, 4, 1),
        Manpower = new WorkforceDashboardService.ManpowerSummary
        {
            FirstHeadcount = 120, SecondHeadcount = 80, FirstSupport = 10, SecondSupport = 5,
            Support =
            [
                new("Sick leave support", "#e5532d", 6, 3),
                new("Trainer", "javascript:alert(1)", 4, 2)
            ],
            Rows =
            [
                new("PSL-B", 100, 90, WorkforceDashboardService.ManpowerRowKind.Line),
                new("PSL-A", 30, 30, WorkforceDashboardService.ManpowerRowKind.Line),
                new("TTL. 51T-52T", 130, 120, WorkforceDashboardService.ManpowerRowKind.Subtotal),
                new("TTL. 53T-55T", 20, 25, WorkforceDashboardService.ManpowerRowKind.Staff),
                new("Total.51T-55T ( Direct Labor)", 150, 145, WorkforceDashboardService.ManpowerRowKind.GrandTotal)
            ]
        }
    };

    static IXLWorksheet Open(WorkforceDashboardService.Report report) =>
        new XLWorkbook(new MemoryStream(ReportExcel.Summary(report))).Worksheet("Summary");

    [Fact]
    public void Headcount_table_has_the_dashboard_rows_and_colours()
    {
        var sheet = Open(SampleReport());
        Assert.Equal("Summary · March 2026", sheet.Cell(1, 1).GetString());
        Assert.Equal("Headcount as of 31 Mar 2026", sheet.Cell(2, 1).GetString());
        Assert.Equal(["Category", "Chemical Line", "Assembly Line", "Total"], Enumerable.Range(1, 4).Select(c => sheet.Cell(5, c).GetString()));
        Assert.Equal("FF3B82F6", sheet.Cell(5, 4).Style.Fill.BackgroundColor.Color.ToArgb().ToString("X8"));

        // Special group rows keep the group's own colour; an unsafe colour falls back to grey.
        Assert.Equal(("Sick leave support", 6, 3, 9), (sheet.Cell(6, 1).GetString(), sheet.Cell(6, 2).GetValue<int>(), sheet.Cell(6, 3).GetValue<int>(), sheet.Cell(6, 4).GetValue<int>()));
        Assert.Equal("FFE5532D", sheet.Cell(6, 2).Style.Font.FontColor.Color.ToArgb().ToString("X8"));
        Assert.Equal("FF6C757D", sheet.Cell(7, 2).Style.Font.FontColor.Color.ToArgb().ToString("X8"));

        Assert.Equal(("Total support", 10, 5, 15), (sheet.Cell(8, 1).GetString(), sheet.Cell(8, 2).GetValue<int>(), sheet.Cell(8, 3).GetValue<int>(), sheet.Cell(8, 4).GetValue<int>()));
        Assert.Equal(("Normal Operator", 110, 75, 185), (sheet.Cell(9, 1).GetString(), sheet.Cell(9, 2).GetValue<int>(), sheet.Cell(9, 3).GetValue<int>(), sheet.Cell(9, 4).GetValue<int>()));
        Assert.Equal(("Total Operator", 120, 80, 200), (sheet.Cell(10, 1).GetString(), sheet.Cell(10, 2).GetValue<int>(), sheet.Cell(10, 3).GetValue<int>(), sheet.Cell(10, 4).GetValue<int>()));
        Assert.Equal("FF1F6FE5", sheet.Cell(9, 1).Style.Fill.BackgroundColor.Color.ToArgb().ToString("X8"));
    }

    [Fact]
    public void Station_table_has_targets_actuals_and_coloured_balances()
    {
        var sheet = Open(SampleReport());
        // Headcount block: title 4, header 5, rows 6-10, note 11; blank 12; station title 13, header 14, rows from 15.
        Assert.Equal("Work Station Performance", sheet.Cell(13, 1).GetString());
        Assert.Equal(["W/S", "New target", "Actual", "Balance"], Enumerable.Range(1, 4).Select(c => sheet.Cell(14, c).GetString()));

        Assert.Equal(("PSL-B", 100, 90, -10), (sheet.Cell(15, 1).GetString(), sheet.Cell(15, 2).GetValue<int>(), sheet.Cell(15, 3).GetValue<int>(), sheet.Cell(15, 4).GetValue<int>()));
        Assert.Equal("FFFDE0E7", sheet.Cell(15, 4).Style.Fill.BackgroundColor.Color.ToArgb().ToString("X8"));
        Assert.Contains("(", sheet.Cell(15, 4).Style.NumberFormat.Format);
        Assert.Equal("FFE6E9F0", sheet.Cell(16, 4).Style.Fill.BackgroundColor.Color.ToArgb().ToString("X8"));
        Assert.Equal("FFFFF27A", sheet.Cell(17, 1).Style.Fill.BackgroundColor.Color.ToArgb().ToString("X8"));

        // The 53T-55T row has no balance target, so it shows a dash, as on the Dashboard.
        Assert.Equal("-", sheet.Cell(18, 4).GetString());
        Assert.Equal("FF0F5FD6", sheet.Cell(19, 1).Style.Fill.BackgroundColor.Color.ToArgb().ToString("X8"));
        Assert.Equal(-5, sheet.Cell(19, 4).GetValue<int>());
    }

    [Fact]
    public void Empty_support_list_and_a_month_with_no_days_still_produce_a_sheet()
    {
        var report = new WorkforceDashboardService.Report { Month = "2026-03", PeriodEnd = new DateOnly(2026, 3, 1) };
        var sheet = Open(report);
        Assert.Equal("No reporting days yet", sheet.Cell(2, 1).GetString());
        Assert.Contains("No active special groups", sheet.Cell(6, 1).GetString());
    }
}
