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
    public void Period_options_are_mtd_then_monday_to_sunday_weeks_cut_at_the_month_boundaries()
    {
        // October 2026 starts on a Thursday; today is Wed 14th, so weeks 4 and 5 have not started.
        var options = WorkforceDashboardService.PeriodOptions(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 14));
        Assert.Equal(["mtd", "w1", "w2", "w3", "w4", "w5"], options.Select(x => x.Key));
        Assert.Equal(["01/10-04/10", "05/10-11/10", "12/10-18/10", "19/10-25/10", "26/10-31/10"], options.Skip(1).Select(x => x.Range));
        Assert.Equal(new DateOnly(2026, 10, 14), options[0].AsOf);
        Assert.Equal(new DateOnly(2026, 10, 4), options[1].AsOf);
        Assert.Equal(new DateOnly(2026, 10, 14), options[3].AsOf);   // the running week stops at today
        Assert.Equal([true, true, true, true, false, false], options.Select(x => x.Available));
    }

    [Fact]
    public async Task Headcount_follows_the_chosen_week_and_falls_back_to_mtd_for_a_bad_or_future_week()
    {
        // March 2026: week 1 is 01-01 Mar (Sun), week 2 is 02-08 Mar. Joined in week 2, left on Mar 5 in week 1's neighbour.
        var early = Hire("Pellet Line");
        var joiner = Hire("Pellet Line"); joiner.StartDate = new DateOnly(2026, 3, 4);
        var leaver = Hire("Pellet Line"); leaver.ResignedDate = new DateOnly(2026, 3, 5); leaver.EmploymentState = EmploymentState.Resigned;
        db.SaveChanges();

        Assert.Equal(2, (await service.LoadAsync("2026-03", null, "w1")).Manpower.FirstHeadcount);   // 1 Mar: early + leaver
        Assert.Equal(2, (await service.LoadAsync("2026-03", null, "w2")).Manpower.FirstHeadcount);   // 8 Mar: early + joiner
        var wrong = (await service.LoadAsync("2026-03", null, "w99")).Manpower;
        Assert.Equal("mtd", wrong.Period);
        Assert.Equal(new DateOnly(2026, 3, 31), wrong.AsOf);
        var week = (await service.LoadAsync("2026-03", null, "W2")).Manpower;
        Assert.Equal(("w2", "Week 2", new DateOnly(2026, 3, 8)), (week.Period, week.PeriodLabel, week.AsOf!.Value));
    }

    [Fact]
    public async Task A_week_limits_the_period_but_the_resigned_list_and_turnover_split_as_expected()
    {
        var early = Hire("Pellet Line"); early.ResignedDate = new DateOnly(2026, 3, 3); early.EmploymentState = EmploymentState.Resigned;   // week 1
        var late = Hire("Polymer"); late.ResignedDate = new DateOnly(2026, 3, 20); late.EmploymentState = EmploymentState.Resigned;           // week 4
        Hire("Polymer");
        db.SaveChanges();

        var week = await service.LoadAsync("2026-03", null, "w4");   // 16-22 Mar (1 Mar is a Sunday, so week 1 is a single day)
        Assert.Equal((new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 23)), (week.PeriodStart, week.PeriodEnd));
        Assert.Equal([late.EmployeeId, early.EmployeeId], week.ResignedEmployees.Select(x => x.Code));   // whole month, newest first
        Assert.Equal(1 * 100m / (1 + 0.5m), week.TurnoverRate);   // only the 20 Mar resignation; headcount on 22 Mar is the stayer

        var mtd = await service.LoadAsync("2026-03", null);
        Assert.Equal(new DateOnly(2026, 3, 1), mtd.PeriodStart);
        Assert.Equal(2 * 100m / (1 + 1m), mtd.TurnoverRate);
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
