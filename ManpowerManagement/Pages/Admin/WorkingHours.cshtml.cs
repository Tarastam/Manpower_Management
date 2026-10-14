using System.Globalization;
using System.Text;
using ManpowerManagement.Data;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Pages.Admin;

// Read-only view of the daily hour summaries (Total / Indirect / Direct) that DailyHoursService stores per day, workshop and shift.
public class WorkingHoursModel(AppDbContext db, AttendanceStagingStore staging, AttendanceFlushService flush) : PageModel
{
    [TempData] public string? FlushMessage { get; set; }
    public int PendingCount => staging.PendingCount;

    // Copies the staged attendance edits to SQL now, then recounts the affected days.
    public async Task<IActionResult> OnPostFlushAsync()
    {
        try
        {
            var pending = await flush.RunNowAsync(DateTime.Now, HttpContext.RequestAborted);
            FlushMessage = pending == 0 ? "ไม่มีข้อมูลค้างใน staging" : $"ส่งข้อมูลเข้า SQL แล้ว ({pending:N0} รายการ)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FlushMessage = "ส่งข้อมูลเข้า SQL ไม่สำเร็จ ข้อมูลยังอยู่ใน staging: " + ex.Message;
        }
        return RedirectToPage();
    }

    public const string StatusOk = "ok", StatusReview = "review", StatusCorrected = "corrected";
    static readonly string[] ThaiMonths = ["ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค."];

    public record HourRow(DateOnly Date, string Workshop, string Shift, decimal Total, decimal Indirect, bool IsManual, string Status,
        List<string> Issues, int DirectCount, int IndirectCount, int I1067, int I841, int I733, string EnteredBy, DateTime? CalculatedAt)
    {
        public decimal Direct => Total - Indirect;
        public string SourceText => IsManual ? "แก้ไขด้วยมือ" : "คำนวณอัตโนมัติ";
    }

    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int? WorkshopId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Shift { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    public List<HourRow> Rows { get; set; } = [];
    public List<SelectListItem> WorkshopOptions { get; set; } = [];
    public List<SelectListItem> ShiftOptions { get; set; } = [];
    public DateTime? LastCalculatedAt { get; set; }
    public decimal TotalHours => Rows.Sum(r => r.Total);
    public decimal IndirectHours => Rows.Sum(r => r.Indirect);
    public decimal DirectHours => TotalHours - IndirectHours;
    public int ReviewCount => Rows.Count(r => r.Status == StatusReview);

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    // CSV for Excel: UTF-8 with a BOM so the Thai text opens correctly.
    public async Task<IActionResult> OnGetExportAsync()
    {
        await LoadAsync();
        var csv = new StringBuilder("Date,Workshop,Shift,Total hours,Indirect hours,Direct hours,Indirect source,Status,Notes\r\n");
        foreach (var r in Rows)
            csv.Append(string.Join(',', r.Date.ToString("yyyy-MM-dd"), Csv(r.Workshop), Csv(r.Shift),
                r.Total.ToString("0.00", CultureInfo.InvariantCulture), r.Indirect.ToString("0.00", CultureInfo.InvariantCulture), r.Direct.ToString("0.00", CultureInfo.InvariantCulture),
                Csv(r.SourceText), Csv(StatusText(r.Status)), Csv(string.Join("; ", r.Issues)))).Append("\r\n");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"working-hours_{From:yyyyMMdd}-{To:yyyyMMdd}.csv");
    }

    public static string StatusText(string status) => status switch
    {
        StatusReview => "ต้องตรวจ",
        StatusCorrected => "แก้ไขด้วยมือ",
        _ => "ปกติ"
    };

    // "02 ต.ค. 07:03" — the format the screen shows for the last calculation time.
    public static string ThaiDayMonth(DateOnly d) => $"{d.Day:00} {ThaiMonths[d.Month - 1]}";
    public static string ThaiDateTime(DateTime t) => $"{ThaiDayMonth(DateOnly.FromDateTime(t))} {t:HH:mm}";

    static string Csv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    async Task LoadAsync()
    {
        var today = BusinessDateService.Current();
        To ??= today;
        From ??= new DateOnly(To.Value.Year, To.Value.Month, 1);
        if (From > To) (From, To) = (To, From);
        var from = From.Value; var to = To.Value;

        // Only closed business days carry hour totals.
        var closed = db.ShiftManpowers.AsNoTracking().Where(m => m.TotalHours != null);
        LastCalculatedAt = (await closed.MaxAsync(m => m.CalculatedAtUtc))?.ToLocalTime();
        WorkshopOptions = (await closed.Select(m => new { m.WorkshopId, m.Workshop!.Name }).Distinct().OrderBy(w => w.Name).ToListAsync())
            .Select(w => new SelectListItem(w.Name, w.WorkshopId.ToString())).ToList();
        ShiftOptions = (await closed.Select(m => m.Shift).Distinct().OrderBy(s => s).ToListAsync())
            .Select(s => new SelectListItem(s, s)).ToList();

        var query = closed.Where(m => m.BusinessDate >= from && m.BusinessDate <= to);
        if (WorkshopId is int workshopId) query = query.Where(m => m.WorkshopId == workshopId);
        if (!string.IsNullOrEmpty(Shift)) query = query.Where(m => m.Shift == Shift);

        var rows = (await query.OrderBy(m => m.BusinessDate).ThenBy(m => m.Workshop!.Name).ThenBy(m => m.Shift)
            .Select(m => new { m.BusinessDate, Workshop = m.Workshop!.Name, m.Shift, m.TotalHours, m.IndirectHours, m.IndirectHoursIsManual,
                m.DirectCount, m.IndirectCount, m.Indirect1067Count, m.Indirect841Count, m.Indirect733Count, m.EnteredBy, m.CalculatedAtUtc })
            .ToListAsync())
            .Select(m =>
            {
                var total = m.TotalHours ?? 0m; var indirect = m.IndirectHours ?? 0m;
                var issues = new List<string>();
                if (indirect > total) issues.Add("Indirect hours มากกว่า Total hours (Direct ติดลบ)");
                // Day shift is entirely indirect, so only the other shifts depend on the keyed hour-band headcount.
                if (m.Shift != LeaderSessionService.DayShift && !m.IndirectHoursIsManual && m.EnteredBy == "")
                    issues.Add("หัวหน้างานยังไม่ได้กรอกจำนวนคนกลุ่ม Indirect (10.67 / 8.41 / 7.33) — Indirect นับเฉพาะชั่วโมงหัวหน้า");
                var status = issues.Count > 0 ? StatusReview : m.IndirectHoursIsManual ? StatusCorrected : StatusOk;
                return new HourRow(m.BusinessDate, m.Workshop, m.Shift, total, indirect, m.IndirectHoursIsManual, status, issues,
                    m.DirectCount, m.IndirectCount, m.Indirect1067Count, m.Indirect841Count, m.Indirect733Count, m.EnteredBy, m.CalculatedAtUtc?.ToLocalTime());
            });
        Rows = (Status is StatusOk or StatusReview or StatusCorrected ? rows.Where(r => r.Status == Status) : rows).ToList();
    }
}
