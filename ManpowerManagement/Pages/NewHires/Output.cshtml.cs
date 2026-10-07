using System.Globalization;
using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Pages.NewHires;

// Leaders (and Admin) key each new hire's output in thousand pieces (kps) for a business day.
public class OutputModel(AppDbContext db, NewHireMonitorService monitor) : PageModel
{
    // Leaders correct recent days; Admin can backfill further.
    const int LeaderBackdays = 7;
    const int AdminBackdays = 120;
    public const decimal MaxKps = 9999m;

    public class OutputInput { public int EmployeeId { get; set; } public string? Kps { get; set; } }
    public record OutputRow(int EmployeeId, string Code, string Name, string Workshop, string Shift, string Process, int TenureDay, decimal? Kps);

    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }
    [BindProperty] public List<OutputInput> Inputs { get; set; } = [];
    [TempData] public string? Message { get; set; }
    public string? Error { get; set; }
    public NewHireAccess Access { get; private set; } = new();
    public DateOnly Today { get; private set; }
    public DateOnly MinDate { get; private set; }
    public List<OutputRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (await GateAsync() is { } gate) return gate;
        await LoadRowsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await GateAsync(strictDate: true) is { } gate) return gate;
        var day = Date!.Value;
        var allowed = (await monitor.MonitoredEmployeesAsync(Access, null, Today, forWriting: true)).Where(e => e.StartDate <= day).ToDictionary(e => e.Id);
        var allowedIds = allowed.Keys.ToList();
        var existing = await db.NewHireOutputs.Where(o => o.BusinessDate == day && allowedIds.Contains(o.EmployeeId)).ToDictionaryAsync(o => o.EmployeeId);
        int saved = 0, cleared = 0;
        foreach (var input in Inputs.GroupBy(i => i.EmployeeId).Select(g => g.First()).Where(i => allowed.ContainsKey(i.EmployeeId)))
        {
            var text = (input.Kps ?? "").Trim().Replace(',', '.');
            existing.TryGetValue(input.EmployeeId, out var row);
            if (text.Length == 0)
            {
                if (row is not null) { db.NewHireOutputs.Remove(row); cleared++; }
                continue;
            }
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var kps) || kps < 0 || kps > MaxKps)
            {
                Error = $"{allowed[input.EmployeeId].FullName}: enter kps as a number from 0 to {MaxKps:0}.";
                db.ChangeTracker.Clear();
                await LoadRowsAsync();
                return Page();
            }
            if (row is null) { row = new NewHireOutput { EmployeeId = input.EmployeeId, BusinessDate = day }; db.NewHireOutputs.Add(row); }
            else if (row.Kps == kps) continue;
            row.Kps = kps; row.EnteredBy = Access.Actor; row.UpdatedAtUtc = DateTime.UtcNow; saved++;
        }
        if (saved + cleared > 0)
            db.AuditLogs.Add(new AuditLog { EntityName = "NewHireOutput", EntityKey = day.ToString("yyyy-MM-dd"), Action = "Saved output", Actor = Access.Actor, Detail = $"{saved} saved, {cleared} cleared" });
        await db.SaveChangesAsync();
        Message = $"Output for {NewHireText.Date(day)} saved: {saved} entered" + (cleared > 0 ? $", {cleared} cleared." : ".");
        return RedirectToPage(new { date = day.ToString("yyyy-MM-dd") });
    }

    // A save must never land on a different day than the one shown, so a bad date is rejected there instead of reset.
    async Task<IActionResult?> GateAsync(bool strictDate = false)
    {
        Access = await monitor.ResolveAccessAsync(HttpContext);
        if (!Access.CanView) return RedirectToPage("/Attendance/CheckIn", new { returnPage = "NewHires" });
        // HR approvers can read the monitor but only leaders and Admin key output.
        if (!Access.CanWrite) { Message = "Only leaders and Admin can enter output."; return RedirectToPage("/NewHires/Index"); }
        Today = BusinessDateService.Current();
        MinDate = Today.AddDays(-(Access.IsAdmin ? AdminBackdays : LeaderBackdays));
        if (Date is not DateOnly picked || picked > Today || picked < MinDate)
        {
            if (strictDate) return BadRequest("That business date cannot be edited.");
            Date = Today;
        }
        return null;
    }

    async Task LoadRowsAsync()
    {
        var day = Date!.Value;
        var employees = (await monitor.MonitoredEmployeesAsync(Access, null, Today, forWriting: true)).Where(e => e.StartDate <= day).ToList();
        var workshops = (await db.Workshops.AsNoTracking().ToListAsync()).ToDictionary(w => w.Id, w => w.Name);
        var ids = employees.Select(e => e.Id).ToList();
        var entered = await db.NewHireOutputs.AsNoTracking().Where(o => o.BusinessDate == day && ids.Contains(o.EmployeeId)).ToDictionaryAsync(o => o.EmployeeId, o => o.Kps);
        Rows = employees.OrderBy(e => workshops.GetValueOrDefault(e.CurrentWorkshopId)).ThenBy(e => e.Shift).ThenBy(e => e.FullName)
            .Select(e => new OutputRow(e.Id, e.EmployeeId, e.FullName, workshops.GetValueOrDefault(e.CurrentWorkshopId) ?? "Unknown", e.Shift, e.Process,
                day.DayNumber - e.StartDate.DayNumber, entered.TryGetValue(e.Id, out var kps) ? kps : null)).ToList();
    }
}
