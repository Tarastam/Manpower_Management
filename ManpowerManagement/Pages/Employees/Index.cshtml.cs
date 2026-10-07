using System.Text;
using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Pages.Employees;

public class IndexModel(AppDbContext db, LeaderSessionService leaderSessions) : PageModel
{
    public class EmployeeInput { public int? Id { get; set; } public string EmployeeId { get; set; } = ""; public string FullName { get; set; } = ""; public string JobGrade { get; set; } = ""; public string Process { get; set; } = ""; public int WorkshopId { get; set; } public string Shift { get; set; } = "Day"; public string Accommodation { get; set; } = ""; public string TelephoneNumber { get; set; } = ""; public EmploymentState EmploymentState { get; set; } = EmploymentState.Active; public string Remark { get; set; } = ""; public List<int> GroupIds { get; set; } = []; public DateOnly? StartDate { get; set; } public bool? IsNewEmployee { get; set; } }
    public class BatchEmployeeInput { public string EmployeeId { get; set; } = ""; public string FirstName { get; set; } = ""; public string LastName { get; set; } = ""; public string JobGrade { get; set; } = ""; public string Process { get; set; } = ""; public string Accommodation { get; set; } = ""; public string TelephoneNumber { get; set; } = ""; public string Status { get; set; } = "Active"; public string Remark { get; set; } = ""; public string Groups { get; set; } = ""; public string StartDate { get; set; } = ""; }
    public class GridEmployeeInput { public int Id { get; set; } public string EmployeeId { get; set; } = ""; public string FullName { get; set; } = ""; public string JobGrade { get; set; } = ""; public string Process { get; set; } = ""; public int WorkshopId { get; set; } public string Shift { get; set; } = "Day"; public string Accommodation { get; set; } = ""; public string TelephoneNumber { get; set; } = ""; public string EmploymentState { get; set; } = "Active"; public string Remark { get; set; } = ""; public List<string> Groups { get; set; } = []; public string StartDate { get; set; } = ""; public string IsNewEmployee { get; set; } = "No"; }
    [BindProperty] public EmployeeInput Input { get; set; } = new();
    [BindProperty] public List<BatchEmployeeInput> BatchRows { get; set; } = [];
    [BindProperty] public string BatchWorkshop { get; set; } = "";
    [BindProperty] public string BatchShift { get; set; } = "";
    // "yes" or "no": must be chosen before new rows can be saved; it only applies to employees created by this save.
    [BindProperty] public string BatchIsNew { get; set; } = "";
    [BindProperty] public List<GridEmployeeInput> GridRows { get; set; } = [];
    public List<Employee> Employees { get; set; } = [];
    public List<SelectListItem> WorkshopOptions { get; set; } = [];
    public List<SelectListItem> DayWorkshopOptions { get; set; } = [];
    public List<SelectListItem> GroupOptions { get; set; } = [];
    [TempData] public string? Message { get; set; }
    public string? Error { get; set; }
    public bool CanDelete => User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Approver);

    // Choosing No is open to everyone, but setting or changing the New Employee tag to Yes is limited: Admin may tag any
    // line, a checked-in leader only their own line (the same workshops they can key attendance for).
    bool tagScopeLoaded, tagAll;
    HashSet<int>? tagScope;
    public string TagNote { get; private set; } = "";
    public bool TagNeedsCheckIn { get; private set; }
    bool CanTag(int workshopId) => tagAll || tagScope?.Contains(workshopId) == true;
    async Task LoadTagScopeAsync()
    {
        if (tagScopeLoaded) return;
        tagScopeLoaded = true;
        if (User.IsInRole(Roles.Admin)) { tagAll = true; TagNote = "New Employee tag: as Admin you can tag employees in every line."; return; }
        var leader = await leaderSessions.ReadAsync(HttpContext, db);
        if (leader is null) { TagNeedsCheckIn = true; TagNote = "New Employee tag: choosing Yes, or changing the tag, needs an Admin login or a leader check-in for that line."; return; }
        tagScope = WorkforceDashboardService.DescendantIds(await db.Workshops.AsNoTracking().ToListAsync(), leader.WorkshopId);
        TagNote = $"New Employee tag: as leader of {leader.WorkshopName} you can tag employees in that line only.";
    }
    // Status is never edited here: an employee becomes Resigned only through an approved resignation ticket.
    const string ResignedByTicketOnly = "Status cannot be set here. Leave it blank or Active; an employee becomes Resigned only through an approved Resignation Ticket, on its effective date.";
    const string TagDenied = "Only Admin, or a leader checked in for this workshop's line, can set New Employee to Yes or change the tag. Log in as Admin or use Leader check-in first.";

    public async Task OnGetAsync(int? editId) { await Load(); AddBlankBatchRows(); if (editId is int id && Employees.FirstOrDefault(x => x.Id == id) is Employee e) { Input = FromEmployee(e); if (e.CurrentWorkshop is Workshop current && WorkshopOptions.All(x => x.Value != current.Id.ToString())) WorkshopOptions.Add(new SelectListItem(current.Name, current.Id.ToString())); } }
    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.EmployeeId) || string.IsNullOrWhiteSpace(Input.FullName) || Input.WorkshopId == 0) return await ShowError("Emp ID, Name and Workshop are required.");
        if (!await db.Workshops.AnyAsync(x => x.Id == Input.WorkshopId)) return await ShowError("Select a valid Workshop.");
        if (Input.Shift is not ("A" or "B" or "C" or "Day")) return await ShowError("Select a valid Shift.");
        if (Input.Shift == "Day")
        {
            var dayNames = SeedData.DayWorkshops.Select(x => x.Name).ToList();
            var chosen = await db.Workshops.Where(x => x.Id == Input.WorkshopId).Select(x => x.Name).FirstAsync();
            if (!dayNames.Contains(chosen) && !await db.Employees.AnyAsync(x => x.Id == Input.Id && x.CurrentWorkshopId == Input.WorkshopId)) return await ShowError("Select a valid Workshop for the Day shift.");
            Input.Process = ""; Input.GroupIds = [];
        }
        if (Input.IsNewEmployee is null) return await ShowError("Choose whether this is a New Employee (Yes or No).");
        if (Input.IsNewEmployee == true && Input.StartDate is null) return await ShowError("Start Date is required for a New Employee. Enter the day they actually started.");
        if (Input.StartDate is DateOnly start && !StartDateInRange(start)) return await ShowError(StartDateRangeMessage);
        var employee = Input.Id is int id ? await db.Employees.FindAsync(id) : null;
        if (await db.Employees.AnyAsync(x => x.EmployeeId == Input.EmployeeId && x.Id != Input.Id)) return await ShowError($"Emp ID '{Input.EmployeeId}' already exists.");
        if (employee is null ? Input.IsNewEmployee == true : employee.IsNewEmployee != Input.IsNewEmployee)
        {
            await LoadTagScopeAsync();
            if (!CanTag(Input.WorkshopId) || (employee is not null && !CanTag(employee.CurrentWorkshopId))) return await ShowError(TagDenied);
        }
        if (employee is null) { employee = new Employee { EmployeeId = Input.EmployeeId, StartDate = Input.StartDate ?? DateOnly.FromDateTime(DateTime.Today) }; db.Employees.Add(employee); }
        else { employee.EmployeeId = Input.EmployeeId; if (Input.StartDate is DateOnly editedStart) employee.StartDate = editedStart; }
        SetNewEmployeeTag(employee, Input.IsNewEmployee.Value);
        Apply(Input, employee); AddHistory(employee, "Employee management"); await SyncGroups(employee, Input.GroupIds); await db.SaveChangesAsync(); Message = "Employee saved."; return RedirectToPage();
    }
    public async Task<IActionResult> OnPostBatchAsync()
    {
        var rows = BatchRows.Where(row => !string.IsNullOrWhiteSpace(row.EmployeeId) || !string.IsNullOrWhiteSpace(row.FirstName) || !string.IsNullOrWhiteSpace(row.LastName) || !string.IsNullOrWhiteSpace(row.JobGrade) || !string.IsNullOrWhiteSpace(row.Process) || !string.IsNullOrWhiteSpace(row.Accommodation) || !string.IsNullOrWhiteSpace(row.TelephoneNumber) || !string.IsNullOrWhiteSpace(row.Remark)).ToList();
        if (rows.Count == 0) return await ShowError("Paste at least one employee row from Excel.");
        if (string.IsNullOrWhiteSpace(BatchWorkshop) || string.IsNullOrWhiteSpace(BatchShift)) return await ShowError("Select a Workshop and Shift above the grid before saving.");
        var ids = rows.Select(x => x.EmployeeId.Trim()).ToList();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count) return await ShowError("Each pasted row needs a unique Emp ID.");
        if (rows.Any(row => string.IsNullOrWhiteSpace(row.FirstName))) return await ShowError("Each pasted row needs a First Name.");
        var workshop = await db.Workshops.FirstOrDefaultAsync(x => x.Name == BatchWorkshop.Trim()); if (workshop is null) return await ShowError($"Workshop '{BatchWorkshop}' was not found.");
        var badStatus = rows.Select(x => (x.Status ?? "").Trim()).FirstOrDefault(x => x.Length > 0 && !x.Equals(nameof(EmploymentState.Active), StringComparison.OrdinalIgnoreCase)); if (badStatus is not null) return await ShowError(ResignedByTicketOnly);
        if (BatchShift.Trim() is not ("A" or "B" or "C" or "Day")) return await ShowError("Select Shift A, B, C or Day above the grid.");
        if (BatchIsNew is not ("yes" or "no")) return await ShowError("Choose New Employee Yes or No above the grid.");
        var startDates = new List<DateOnly?>();
        foreach (var row in rows)
        {
            if (!TryParseStartDate(row.StartDate, out var parsedStart)) return await ShowError($"Start Date '{row.StartDate}' for Emp ID '{row.EmployeeId}' is not a date. Use yyyy-MM-dd or dd/MM/yyyy.");
            if (parsedStart is DateOnly s && !StartDateInRange(s)) return await ShowError($"Emp ID '{row.EmployeeId}': {StartDateRangeMessage}");
            startDates.Add(parsedStart);
        }
        var allGroups = await db.SpecialGroups.ToListAsync();
        var rowGroupNames = rows.Select(row => ParseGroupNames(row.Groups)).ToList();
        var unknownGroup = rowGroupNames.SelectMany(names => names).FirstOrDefault(name => !allGroups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (unknownGroup is not null) return await ShowError($"Special group '{unknownGroup}' was not found. Add it on the Special Groups page first.");
        var idSet = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var existing = (await db.Employees.Where(x => idSet.Contains(x.EmployeeId)).ToListAsync()).ToDictionary(x => x.EmployeeId, StringComparer.OrdinalIgnoreCase); var added = 0;
        if (BatchIsNew == "yes" && rows.Any(r => !existing.ContainsKey(r.EmployeeId.Trim())))
        {
            await LoadTagScopeAsync();
            if (!CanTag(workshop.Id)) return await ShowError(TagDenied);
        }
        // A new employee's age is counted from the day they started, so that date cannot be left to default to today.
        if (BatchIsNew == "yes" && rows.FirstOrDefault(r => !existing.ContainsKey(r.EmployeeId.Trim()) && string.IsNullOrWhiteSpace(r.StartDate)) is { } undated) return await ShowError($"Emp ID '{undated.EmployeeId.Trim()}' is a New Employee, so its Start Date is required.");
        for (var i = 0; i < rows.Count; i++) { var row = rows[i]; var employeeId = row.EmployeeId.Trim(); if (!existing.TryGetValue(employeeId, out var employee)) { employee = new Employee { EmployeeId = employeeId, StartDate = startDates[i] ?? DateOnly.FromDateTime(DateTime.Today) }; SetNewEmployeeTag(employee, BatchIsNew == "yes"); db.Employees.Add(employee); added++; } else if (startDates[i] is DateOnly pastedStart) employee.StartDate = pastedStart; Apply(new EmployeeInput { EmployeeId = employeeId, FullName = $"{(row.FirstName ?? "").Trim()} {(row.LastName ?? "").Trim()}".Trim(), JobGrade = (row.JobGrade ?? "").Trim(), Process = (row.Process ?? "").Trim(), WorkshopId = workshop.Id, Shift = BatchShift.Trim(), Accommodation = (row.Accommodation ?? "").Trim(), TelephoneNumber = (row.TelephoneNumber ?? "").Trim(), Remark = (row.Remark ?? "").Trim() }, employee); AddHistory(employee, "Employee batch grid"); var groupIds = allGroups.Where(g => rowGroupNames[i].Any(name => name.Equals(g.Name, StringComparison.OrdinalIgnoreCase))).Select(g => g.Id).ToList(); await SyncGroups(employee, groupIds); }
        await db.SaveChangesAsync(); Message = $"Saved {rows.Count} employee row(s) ({added} new)."; return RedirectToPage();
    }
    public async Task<IActionResult> OnPostGridSaveAsync()
    {
        var rowIds = GridRows.Select(r => r.Id).ToHashSet();
        var employees = (await db.Employees.ToListAsync()).Where(x => rowIds.Contains(x.Id)).ToDictionary(x => x.Id);
        var workshopIds = (await db.Workshops.ToListAsync()).Select(x => x.Id).ToHashSet();
        var employeeIdOwners = employees.Values.ToDictionary(x => x.EmployeeId, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var allGroups = await db.SpecialGroups.ToListAsync();
        var rowGroupNames = GridRows.Select(row => row.Groups ?? []).ToList();
        var unknownGroup = rowGroupNames.SelectMany(names => names).FirstOrDefault(name => !allGroups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (unknownGroup is not null) return await ShowError($"Special group '{unknownGroup}' was not found. Add it on the Special Groups page first.");
        for (var i = 0; i < GridRows.Count; i++)
        {
            var row = GridRows[i];
            if (!employees.TryGetValue(row.Id, out var employee)) continue;
            if (string.IsNullOrWhiteSpace(row.EmployeeId) || string.IsNullOrWhiteSpace(row.FullName)) return await ShowError("Emp ID and Name are required for every row.");
            if (!workshopIds.Contains(row.WorkshopId)) return await ShowError("Select a valid Workshop for every row.");
            if (row.Shift is not ("A" or "B" or "C" or "Day")) return await ShowError("Select a valid Shift for every row.");
            if (employeeIdOwners.TryGetValue(row.EmployeeId, out var ownerId) && ownerId != row.Id) return await ShowError($"Emp ID '{row.EmployeeId}' already exists.");
            if (!TryParseStartDate(row.StartDate, out var gridStart)) return await ShowError($"Start Date '{row.StartDate}' for Emp ID '{row.EmployeeId}' is not a date. Use yyyy-MM-dd.");
            if (gridStart is DateOnly gs && !StartDateInRange(gs)) return await ShowError($"Emp ID '{row.EmployeeId}': {StartDateRangeMessage}");
            if (row.IsNewEmployee is not ("Yes" or "No")) return await ShowError("Choose Yes or No under New Employee for every row.");
            if (employee.IsNewEmployee != (row.IsNewEmployee == "Yes"))
            {
                await LoadTagScopeAsync();
                if (!CanTag(employee.CurrentWorkshopId) || !CanTag(row.WorkshopId)) return await ShowError($"Emp ID '{row.EmployeeId}': {TagDenied}");
            }
            employeeIdOwners.Remove(employee.EmployeeId);
            employeeIdOwners[row.EmployeeId] = row.Id;
            employee.EmployeeId = row.EmployeeId;
            if (gridStart is DateOnly newStart) employee.StartDate = newStart;
            SetNewEmployeeTag(employee, row.IsNewEmployee == "Yes");
            Apply(new EmployeeInput { EmployeeId = row.EmployeeId, FullName = row.FullName, JobGrade = row.JobGrade, Process = row.Process, WorkshopId = row.WorkshopId, Shift = row.Shift, Accommodation = row.Accommodation, TelephoneNumber = row.TelephoneNumber, Remark = row.Remark }, employee);
            AddHistory(employee, "Employee grid edit");
            var groupIds = allGroups.Where(g => rowGroupNames[i].Any(name => name.Equals(g.Name, StringComparison.OrdinalIgnoreCase))).Select(g => g.Id).ToList();
            await SyncGroups(employee, groupIds);
        }
        await db.SaveChangesAsync(); Message = "Employee grid changes saved."; return RedirectToPage();
    }
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!CanDelete) return Forbid();
        var employee = await db.Employees.FindAsync(id);
        if (employee is null) return RedirectToPage();
        db.Employees.Remove(employee);
        try { await db.SaveChangesAsync(); Message = "Employee deleted."; }
        catch (DbUpdateException) { return await ShowError("Cannot delete this employee because related records (e.g. resignation tickets) still reference them."); }
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostBulkDeleteAsync(List<int> selectedIds)
    {
        if (!CanDelete) return Forbid();
        if (selectedIds is null || selectedIds.Count == 0) return await ShowError("Select at least one employee to delete.");
        var idSet = selectedIds.ToHashSet();
        var employees = await db.Employees.Where(x => idSet.Contains(x.Id)).ToListAsync();
        db.Employees.RemoveRange(employees);
        try { await db.SaveChangesAsync(); Message = $"Deleted {employees.Count} employee(s)."; }
        catch (DbUpdateException) { return await ShowError("Cannot delete one or more selected employees because related records (e.g. resignation tickets) still reference them."); }
        return RedirectToPage();
    }
    public async Task<IActionResult> OnGetExportCsvAsync()
    {
        var employees = await db.Employees.Include(x => x.CurrentWorkshop).Include(x => x.SpecialGroups).ThenInclude(x => x.SpecialGroup).OrderBy(x => x.EmployeeId).ToListAsync();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', new[] { "Emp ID", "Name", "Job Grade", "Process", "Workshop", "Shift", "Accommodation", "Tel. No", "Status", "Start Date", "New Employee", "Special Groups", "Remark" }.Select(CsvField)));
        foreach (var e in employees)
        {
            var groups = string.Join("; ", e.SpecialGroups.Select(x => x.SpecialGroup!.Name));
            sb.AppendLine(string.Join(',', new[] { e.EmployeeId, e.FullName, e.JobGrade, e.Process, e.CurrentWorkshop?.Name ?? "", e.Shift, e.Accommodation, e.TelephoneNumber, e.EmploymentState.ToString(), e.StartDate.ToString("yyyy-MM-dd"), e.IsNewEmployee ? "Yes" : "No", groups, e.Remark }.Select(CsvField)));
        }
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"employees_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
    }
    private static string CsvField(string? value)
    {
        value ??= "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
    private async Task<IActionResult> ShowError(string error) { Error = error; await Load(); AddBlankBatchRows(); return Page(); }
    private void AddBlankBatchRows() { while (BatchRows.Count < 20) BatchRows.Add(new BatchEmployeeInput()); }
    private static EmployeeInput FromEmployee(Employee e) => new() { Id = e.Id, EmployeeId = e.EmployeeId, FullName = e.FullName, JobGrade = e.JobGrade, Process = e.Process, WorkshopId = e.CurrentWorkshopId, Shift = e.Shift, Accommodation = e.Accommodation, TelephoneNumber = e.TelephoneNumber, EmploymentState = e.EmploymentState, Remark = e.Remark, GroupIds = e.SpecialGroups.Select(x => x.SpecialGroupId).ToList(), StartDate = e.StartDate, IsNewEmployee = e.IsNewEmployee };
    private static void Apply(EmployeeInput input, Employee employee) { employee.FullName = input.FullName; employee.JobGrade = input.JobGrade ?? ""; employee.Process = input.Process ?? ""; employee.CurrentWorkshopId = input.WorkshopId; employee.Shift = input.Shift; employee.Accommodation = input.Accommodation ?? ""; employee.TelephoneNumber = input.TelephoneNumber ?? ""; employee.Remark = input.Remark ?? ""; }
    private void AddHistory(Employee employee, string action) => db.EmployeeHistories.Add(new EmployeeHistory { Employee = employee, WorkshopId = employee.CurrentWorkshopId, EmploymentType = employee.EmploymentType, EffectiveDate = DateOnly.FromDateTime(DateTime.Today), ChangedBy = User.Identity?.Name ?? action });
    // The tag is only rewritten when it is new or actually changes, so editing an unrelated field of someone who already
    // passed probation (tag Yes, type Permanent) does not put them back into monitoring.
    private static void SetNewEmployeeTag(Employee employee, bool isNew)
    {
        if (employee.Id != 0 && employee.IsNewEmployee == isNew) return;
        employee.IsNewEmployee = isNew;
        employee.EmploymentType = isNew ? EmploymentType.NewComer : EmploymentType.Permanent;
    }
    // Hires can be entered a little ahead of their first day, but very old or far-future dates are almost always typos.
    private const string StartDateRangeMessage = "Start Date must be within the last 10 years and at most 60 days ahead.";
    private static bool StartDateInRange(DateOnly date) { var today = DateOnly.FromDateTime(DateTime.Today); return date >= today.AddYears(-10) && date <= today.AddDays(60); }
    private static readonly string[] StartDateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd-MMM-yyyy", "d-MMM-yyyy", "d MMM yyyy", "dd MMM yyyy"];
    // A blank cell means "not given": new employees start today and existing ones keep their start date.
    private static bool TryParseStartDate(string? text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateOnly.TryParseExact(text.Trim(), StartDateFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)) return false;
        date = parsed;
        return true;
    }
    private static List<string> ParseGroupNames(string? groups) => (groups ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private async Task SyncGroups(Employee employee, List<int> groupIds)
    {
        if (employee.Id != 0) await db.Entry(employee).Collection(x => x.SpecialGroups).LoadAsync();
        var wantedIds = groupIds.Distinct().ToHashSet();
        employee.SpecialGroups.Where(x => !wantedIds.Contains(x.SpecialGroupId)).ToList().ForEach(x => employee.SpecialGroups.Remove(x));
        var existingIds = employee.SpecialGroups.Select(x => x.SpecialGroupId).ToHashSet();
        foreach (var id in wantedIds.Where(id => !existingIds.Contains(id))) employee.SpecialGroups.Add(new EmployeeSpecialGroup { Employee = employee, SpecialGroupId = id });
    }
    private async Task Load() { await LoadTagScopeAsync(); Employees = await db.Employees.Include(x => x.CurrentWorkshop).Include(x => x.SpecialGroups).ThenInclude(x => x.SpecialGroup).OrderBy(x => x.EmployeeId).ToListAsync(); var workshops = await db.Workshops.ToListAsync(); WorkshopOptions = SeedData.EmployeeWorkshopNames.Select(name => workshops.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).Where(x => x is not null).Select(x => new SelectListItem(x!.Name, x.Id.ToString())).ToList(); if (Input.Id is not null && Employees.FirstOrDefault(x => x.Id == Input.Id)?.CurrentWorkshop is Workshop current && WorkshopOptions.All(x => x.Value != current.Id.ToString())) WorkshopOptions.Add(new SelectListItem(current.Name, current.Id.ToString())); DayWorkshopOptions = SeedData.DayWorkshops.Select(d => (d.Label, w: workshops.FirstOrDefault(x => x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)))).Where(x => x.w is not null).Select(x => new SelectListItem(x.Label, x.w!.Id.ToString())).ToList(); GroupOptions = await db.SpecialGroups.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync(); }
}
