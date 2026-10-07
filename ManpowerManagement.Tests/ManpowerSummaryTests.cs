using Xunit;
using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManpowerManagement.Tests;

// Runs against an in-memory database: the real SQL Server holds production data and must not be used for tests.
public class ManpowerSummaryTests : IDisposable
{
    readonly string stagingPath = Path.Combine(Path.GetTempPath(), $"staging-{Guid.NewGuid():N}.json");
    readonly AppDbContext db;
    readonly WorkforceDashboardService service;
    readonly Dictionary<string, Workshop> workshops = [];
    int next;

    public ManpowerSummaryTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        service = new WorkforceDashboardService(db, new AttendanceStagingStore(stagingPath, NullLogger<AttendanceStagingStore>.Instance));
        var production = Add("Total Production", null);
        var chemical = Add("Total Chemical Line", production);
        var assembly = Add("Total Assembly Line", production);
        foreach (var name in new[] { "Pellet Line", "Pellet Assembly", "Anodization", "Polymer" }) Add(name, chemical);
        var psl = Add("PSL", assembly);
        foreach (var name in new[] { "PSLA", "PSLB" }) Add(name, psl);
        Add("GPSP2", assembly);
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(stagingPath) + "*")) File.Delete(file);
    }

    Workshop Add(string name, Workshop? parent)
    {
        var workshop = new Workshop { Name = name, Parent = parent };
        db.Workshops.Add(workshop);
        workshops[name] = workshop;
        return workshop;
    }

    Employee Hire(string workshop, string grade = "51T", params string[] groups)
    {
        var employee = new Employee
        {
            EmployeeId = $"E{++next}", JobGrade = grade, FullName = $"Person {next}", CurrentWorkshop = workshops[workshop],
            StartDate = new DateOnly(2026, 1, 5)
        };
        foreach (var group in groups)
        {
            var found = db.SpecialGroups.Local.FirstOrDefault(x => x.Name == group) ?? db.SpecialGroups.Add(new SpecialGroup { Name = group }).Entity;
            employee.SpecialGroups.Add(new EmployeeSpecialGroup { SpecialGroup = found });
        }
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    [Fact]
    public async Task Counts_headcount_per_line_and_target_balance()
    {
        Hire("Pellet Line"); Hire("Pellet Line"); Hire("Polymer");
        Hire("PSLB"); Hire("GPSP2");
        Hire("Polymer", "54T"); Hire("PSLA", "56T");   // 53T-55T of the Chemical line; 56T is outside the table
        var resigned = Hire("Anodization");
        resigned.ResignedDate = new DateOnly(2026, 1, 20);
        resigned.EmploymentState = EmploymentState.Resigned;
        db.SaveChanges();

        var summary = (await service.LoadAsync("2026-03", null)).Manpower;

        Assert.Equal(3, summary.FirstHeadcount);
        Assert.Equal(2, summary.SecondHeadcount);
        var pellet = summary.Rows.Single(x => x.Label == "Pellet line");
        Assert.Equal(33, pellet.Target);
        Assert.Equal(2, pellet.Actual);
        Assert.Equal(-31, pellet.Balance);
        var staff = summary.Rows.First(x => x.Kind == WorkforceDashboardService.ManpowerRowKind.Staff);
        Assert.Equal(1, staff.Actual);
        Assert.Null(staff.Balance);
        Assert.Equal(1245, summary.Rows[^1].Target);
        Assert.Equal(6, summary.Rows[^1].Actual);
    }

    [Fact]
    public async Task Support_rows_follow_special_group_tags_and_count_each_person_once()
    {
        Hire("Pellet Line", "52T", "Pregnant");
        Hire("Polymer", "51T", "Sick", "Pregnant");   // two tags, one person
        Hire("Polymer", "51T", "Wire+staff+support");
        Hire("PSLA", "51T", "Acting"); Hire("Pellet Line", "54T", "Pregnant");   // senior grade is not counted as support
        Hire("PSLA");

        var summary = (await service.LoadAsync("2026-03", null)).Manpower;

        WorkforceDashboardService.SupportRow Row(string label) => summary.Support.Single(x => x.Label == label);
        Assert.Equal(2, Row("Pregnant").First);
        Assert.Equal(1, Row("Sick").First);
        Assert.Equal(1, Row("Wire+staff+support").First);
        Assert.Equal(1, Row("Acting").Second);
        Assert.Equal(3, summary.FirstSupport);
        Assert.Equal(1, summary.SecondSupport);
        Assert.Equal(4, summary.TotalSupport);
    }

    [Fact]
    public async Task Lists_people_who_resigned_in_the_month_newest_first_within_the_workshop_filter()
    {
        void Resign(Employee e, DateOnly date) { e.ResignedDate = date; e.EmploymentState = EmploymentState.Resigned; }
        var early = Hire("Pellet Line"); Resign(early, new DateOnly(2026, 3, 3));
        var late = Hire("Polymer"); Resign(late, new DateOnly(2026, 3, 20));
        var otherLine = Hire("PSLB"); Resign(otherLine, new DateOnly(2026, 3, 10));
        var otherMonth = Hire("Polymer"); Resign(otherMonth, new DateOnly(2026, 2, 27));
        db.SaveChanges();

        var all = (await service.LoadAsync("2026-03", null)).ResignedEmployees;
        Assert.Equal([late.EmployeeId, otherLine.EmployeeId, early.EmployeeId], all.Select(x => x.Code));
        Assert.Equal("Polymer", all[0].Workshop);

        var chemical = (await service.LoadAsync("2026-03", workshops["Total Chemical Line"].Id)).ResignedEmployees;
        Assert.Equal([late.EmployeeId, early.EmployeeId], chemical.Select(x => x.Code));
    }

    [Fact]
    public async Task Ignores_the_workshop_filter()
    {
        Hire("Pellet Line"); Hire("PSLB");
        var filtered = (await service.LoadAsync("2026-03", workshops["Pellet Line"].Id)).Manpower;
        Assert.Equal(1, filtered.FirstHeadcount);
        Assert.Equal(1, filtered.SecondHeadcount);
    }
}
