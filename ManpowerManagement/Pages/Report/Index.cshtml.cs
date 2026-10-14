using System.Text.RegularExpressions;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ManpowerManagement.Pages.Report;

// Public on purpose (no authorization rule): anyone can view the reports and download them as Excel.
public class IndexModel(ReportService reports, WorkforceDashboardService dashboard) : PageModel
{
    public const string Attendance = "attendance", Absent = "absent", Hours = "hours", Employees = "employees", Summary = "summary";
    public const int PreviewLimit = 200;
    public const string TotalCode = "Total";

    [BindProperty(SupportsGet = true)] public string Type { get; set; } = Attendance;
    [BindProperty(SupportsGet = true)] public string? Month { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int? WorkshopId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Shift { get; set; }
    [BindProperty(SupportsGet = true)] public string? State { get; set; }
    // Attendance (Monthly) report only.
    [BindProperty(SupportsGet = true)] public int? SpecialGroupId { get; set; }
    // Summary report only: "mtd" or "w1".."w6", see WorkforceDashboardService.PeriodOptions.
    [BindProperty(SupportsGet = true)] public string? Period { get; set; }

    public List<SelectListItem> WorkshopOptions { get; private set; } = [];
    public List<SelectListItem> SpecialGroupOptions { get; private set; } = [];
    public ReportService.AttendanceTable AttendanceTable { get; private set; } = new(default, [], []);
    public ReportService.LeaveTrend LeaveTrend { get; private set; } = new(default, [], []);
    public List<LeaveSeries> LeaveSeriesList { get; private set; } = [];
    public List<ReportService.HoursRow> HoursRows { get; private set; } = [];
    public List<ReportService.EmployeeRow> EmployeeRows { get; private set; } = [];
    public WorkforceDashboardService.Report DashboardReport { get; private set; } = new();
    public int TotalRows => Type switch { Hours => HoursRows.Count, Absent => LeaveTrend.Rows.Count, Employees => EmployeeRows.Count, Summary => DashboardReport.Manpower.Rows.Count, _ => AttendanceTable.Rows.Count };
    public decimal TotalHours => HoursRows.Sum(r => r.Total);
    public decimal IndirectHours => HoursRows.Sum(r => r.Indirect);
    public decimal DirectHours => TotalHours - IndirectHours;
    // Headcount cards show the average people per day (all workshops and shifts shown added up per day, then averaged over the days),
    // because adding the same people up over many days would only give person-days.
    public decimal AvgTotalCount => AvgPerDay(r => r.TotalCount);
    public decimal AvgIndirectCount => AvgPerDay(r => r.IndirectCount);
    public decimal AvgDirectCount => AvgPerDay(r => r.DirectCount);
    decimal AvgPerDay(Func<ReportService.HoursRow, int> count) =>
        HoursRows.Count == 0 ? 0 : (decimal)HoursRows.GroupBy(r => r.Date).Average(g => g.Sum(count));
    public static string[] Shifts => ReportService.Shifts;
    public static string[] ReportShifts => ReportService.ReportShifts;

    // One card and one line chart: the share of all manpower (Day shift excluded) on the given leave types, per day.
    // WithoutAl is the same line with annual leave left out; the chart's "Exclude AL" toggle switches to it.
    public record LeaveSeries(string Code, string Title, string Detail, List<ReportService.LeavePoint> Points, LeaveSeries? WithoutAl = null)
    {
        public decimal Average => Points.Count == 0 ? 0 : Points.Average(p => p.Ratio);
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnGetDownloadAsync()
    {
        await LoadAsync();
        var workshop = WorkshopOptions.FirstOrDefault(o => o.Value == WorkshopId?.ToString())?.Text;
        switch (Type)
        {
            case Summary:
                return File(ReportExcel.Summary(DashboardReport), ReportExcel.ContentType, $"summary_{DashboardReport.Month}{(Period == WorkforceDashboardService.Mtd ? "" : "_" + Period)}.xlsx");
            case Hours:
                return File(ReportExcel.WorkingHours(HoursRows), ReportExcel.ContentType, $"working-hours_{From:yyyyMMdd}-{To:yyyyMMdd}.xlsx");
            case Absent:
                return File(ReportExcel.LeaveTrend(LeaveTrend), ReportExcel.ContentType,
                    $"leave-by-day_{FileSafe(workshop, "all-workshops")}_{FileSafe(Shift, "A-B-C")}_{Month}.xlsx");
            case Employees:
                return File(ReportExcel.Employees(EmployeeRows), ReportExcel.ContentType, $"employees_{DateTime.Now:yyyyMMdd}.xlsx");
            default:
                var group = SpecialGroupOptions.FirstOrDefault(o => o.Value == SpecialGroupId?.ToString())?.Text;
                return File(ReportExcel.Attendance(AttendanceTable), ReportExcel.ContentType,
                    $"attendance_{FileSafe(workshop, "all-workshops")}_{FileSafe(Shift, "all-shifts")}{(group is null ? "" : "_" + FileSafe(group, ""))}_{AttendanceTable.MonthStart:yyyy-MM}.xlsx");
        }
    }

    async Task LoadAsync()
    {
        if (Type is not (Hours or Employees or Absent or Summary)) Type = Attendance;
        if (!string.IsNullOrEmpty(Shift) && !(Type == Employees ? ReportService.Shifts : ReportService.ReportShifts).Contains(Shift)) Shift = null;
        WorkshopOptions = (await reports.WorkshopsAsync()).Select(w => new SelectListItem(w.Name, w.Id.ToString())).ToList();
        switch (Type)
        {
            case Summary:
                // Same tables as the dashboard: they cover the whole production floor, so no workshop or shift filter applies.
                DashboardReport = await dashboard.LoadAsync(Month, null, Period);
                Month = DashboardReport.Month;
                Period = DashboardReport.Manpower.Period;
                break;
            case Hours:
                (From, To) = ReportService.HoursRange(From, To);
                HoursRows = await reports.WorkingHoursAsync(From.Value, To.Value, WorkshopId, Shift);
                break;
            case Absent:
                LeaveTrend = await reports.LeaveTrendAsync(Month, WorkshopId, Shift);
                Month = LeaveTrend.MonthStart.ToString("yyyy-MM");
                LeaveSeriesList =
                [
                    Series(TotalCode, "Total leave", "AL + SL + BL + OL + AB + ML", ReportService.LeaveTypes) with
                    {
                        WithoutAl = Series(TotalCode,"Total leave (excl. AL)", "SL + BL + OL + AB + ML", ReportService.LeaveTypes.Where(t => t != LeaveType.AnnualLeave).ToArray())
                    },
                    Series("AL", "Annual leave (AL)", "AL", [LeaveType.AnnualLeave]),
                    Series("SL", "Sick leave (SL)", "SL", [LeaveType.SickLeave]),
                    Series("BL", "Business leave (BL)", "BL", [LeaveType.BusinessLeave]),
                    Series("OL", "Other leave (OL)", "OL", [LeaveType.OtherLeave]),
                    Series("AB", "Absent (AB)", "AB", [LeaveType.Absent]),
                    Series("ML", "Mother leave (ML)", "ML", [LeaveType.MotherLeave])
                ];
                break;
            case Employees:
                EmployeeRows = await reports.EmployeesAsync(WorkshopId, Shift, State);
                break;
            default:
                SpecialGroupOptions = (await reports.SpecialGroupsAsync()).Select(g => new SelectListItem(g.Name, g.Id.ToString())).ToList();
                if (SpecialGroupId is int groupId && SpecialGroupOptions.All(o => o.Value != groupId.ToString())) SpecialGroupId = null;
                AttendanceTable = await reports.AttendanceMonthAsync(Month, WorkshopId, Shift, specialGroupId: SpecialGroupId);
                Month = AttendanceTable.MonthStart.ToString("yyyy-MM");
                break;
        }
    }

    LeaveSeries Series(string code, string title, string detail, LeaveType[] types) =>
        new(code, title, detail, LeaveTrend.Series(row => types.Sum(row.Of)));

    static string FileSafe(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : Regex.Replace(value.Trim(), @"[^\w\-]+", "-");
}
