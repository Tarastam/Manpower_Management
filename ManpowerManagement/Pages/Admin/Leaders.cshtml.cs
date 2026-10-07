using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Pages.Admin;

// Registers which employee IDs may check in on the Attendance page, and for which workshop.
public class LeadersModel(AppDbContext db) : PageModel
{
    public record LeaderRow(int Id, string EmployeeCode, string Name, bool EmployeeActive, int WorkshopId, string Workshop, bool IsActive);

    [BindProperty] public int? EditId { get; set; }
    [BindProperty] public string EmployeeCode { get; set; } = "";
    [BindProperty] public int WorkshopId { get; set; }
    [BindProperty] public string? LeaderName { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    public List<LeaderRow> Leaders { get; set; } = [];
    public List<SelectListItem> WorkshopOptions { get; set; } = [];
    [TempData] public string? Message { get; set; }
    public string? Error { get; set; }

    // Leaders are assigned to a whole line and pick the workshop and shift on the attendance page.
    public static readonly string[] LineNames = ["Total Chemical Line", "Total Assembly Line"];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var code = EmployeeCode.Trim(); var name = LeaderName?.Trim();
        if (code == "" || !LineNames.Contains(await db.Workshops.Where(w => w.Id == WorkshopId).Select(w => w.Name).FirstOrDefaultAsync() ?? "")) { Error = "Enter an employee ID and a line (Chemical Line or Assembly Line)."; await LoadAsync(); return Page(); }
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.EmployeeId == code && e.EmploymentState == EmploymentState.Active);
        // Leaders outside the employee master can be added, but then a name is required.
        if (employee is null && string.IsNullOrEmpty(name)) { Error = $"{code} is not in the employee master. Enter the leader's name."; await LoadAsync(); return Page(); }
        // A leader may cover several workshops, each registered once; the shift is picked on the attendance page.
        if (await db.AttendanceLeaders.AnyAsync(l => l.EmployeeCode == code && l.WorkshopId == WorkshopId && l.Id != EditId)) { Error = $"{code} is already assigned to this workshop."; await LoadAsync(); return Page(); }
        var isNew = EditId is null;
        var leader = isNew ? db.AttendanceLeaders.Add(new AttendanceLeader()).Entity : await db.AttendanceLeaders.FindAsync(EditId);
        if (leader is null) return RedirectToPage();
        leader.EmployeeCode = code; leader.LeaderName = employee is null ? name : null;
        leader.WorkshopId = WorkshopId; leader.Shift = ""; leader.IsActive = true; leader.UpdatedAtUtc = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog { EntityName = "AttendanceLeader", EntityKey = code, Action = isNew ? "Added" : "Updated", Actor = User.Identity?.Name ?? "Admin", Detail = $"workshop {WorkshopId}" });
        await db.SaveChangesAsync();
        Message = $"{employee?.FullName ?? name} ({code}) can key attendance for {await db.Workshops.Where(w => w.Id == WorkshopId).Select(w => w.Name).FirstAsync()} (all workshops and shifts).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var leader = await db.AttendanceLeaders.FindAsync(id);
        if (leader is null) return RedirectToPage();
        leader.IsActive = !leader.IsActive; leader.UpdatedAtUtc = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog { EntityName = "AttendanceLeader", EntityKey = leader.EmployeeCode, Action = leader.IsActive ? "Enabled" : "Disabled", Actor = User.Identity?.Name ?? "Admin" });
        await db.SaveChangesAsync();
        Message = $"{leader.EmployeeCode} {(leader.IsActive ? "enabled" : "disabled")}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var leader = await db.AttendanceLeaders.FindAsync(id);
        if (leader is not null)
        {
            db.AttendanceLeaders.Remove(leader);
            db.AuditLogs.Add(new AuditLog { EntityName = "AttendanceLeader", EntityKey = leader.EmployeeCode, Action = "Removed", Actor = User.Identity?.Name ?? "Admin" });
            await db.SaveChangesAsync();
            Message = $"{leader.EmployeeCode} removed.";
        }
        return RedirectToPage();
    }

    // Used by the form to show the employee's name before saving.
    public async Task<IActionResult> OnGetLookupAsync(string code)
    {
        var employee = await db.Employees.Include(e => e.CurrentWorkshop).FirstOrDefaultAsync(e => e.EmployeeId == code.Trim() && e.EmploymentState == EmploymentState.Active);
        return new JsonResult(employee is null ? new { ok = false, name = "" } : new { ok = true, name = employee.FullName });
    }

    async Task LoadAsync()
    {
        WorkshopOptions = await db.Workshops.Where(w => LineNames.Contains(w.Name)).OrderBy(w => w.Name).Select(w => new SelectListItem(w.Name, w.Id.ToString())).ToListAsync();
        var query = from l in db.AttendanceLeaders
                    join e in db.Employees on l.EmployeeCode equals e.EmployeeId into emp
                    from e in emp.DefaultIfEmpty()
                    select new { l, Name = e == null ? l.LeaderName ?? "" : e.FullName, EmployeeActive = e != null && e.EmploymentState == EmploymentState.Active, Workshop = l.Workshop!.Name };
        if (!string.IsNullOrWhiteSpace(Search)) query = query.Where(x => x.l.EmployeeCode.Contains(Search) || x.Name.Contains(Search) || x.Workshop.Contains(Search));
        Leaders = (await query.OrderBy(x => x.Name).ThenBy(x => x.l.EmployeeCode).ThenBy(x => x.Workshop).ToListAsync())
            .Select(x => new LeaderRow(x.l.Id, x.l.EmployeeCode, x.Name, x.EmployeeActive, x.l.WorkshopId, x.Workshop, x.l.IsActive)).ToList();
    }
}
