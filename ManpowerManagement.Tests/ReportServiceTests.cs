using ClosedXML.Excel;
using Xunit;
using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Pages.Attendance;
using ManpowerManagement.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManpowerManagement.Tests;

// Runs against an in-memory database: the real SQL Server holds production data and must not be used for tests.
public class ReportServiceTests : IDisposable
{
    const string Month = "2026-03";
    // 2026-03-03 is an OFF day for the workshop; every other day of March is a working day.
    static readonly DateOnly OffDay = new(2026, 3, 3);
    readonly string stagingPath = Path.Combine(Path.GetTempPath(), $"staging-{Guid.NewGuid():N}.json");
    readonly AppDbContext db;
    readonly AttendanceStagingStore store;
    readonly ReportService service;
    readonly int workshopId;

    public ReportServiceTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        store = new AttendanceStagingStore(stagingPath, NullLogger<AttendanceStagingStore>.Instance);
        service = new ReportService(db, store);
        workshopId = AddWorkshop("PSL");
    }

    public void Dispose()
    {
        db.Dispose();
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(stagingPath) + "*")) File.Delete(file);
    }

    int AddWorkshop(string name)
    {
        var workshop = new Workshop { Name = name };
        db.Workshops.Add(workshop);
        db.SaveChanges();
        for (var d = new DateOnly(2026, 3, 1); d.Month == 3; d = d.AddDays(1))
            db.WorkCalendars.Add(new WorkCalendar { WorkshopId = workshop.Id, WorkDate = d, IsWorkingDay = d != OffDay });
        db.SaveChanges();
        return workshop.Id;
    }

    Employee AddEmployee(string code, string shift = "A", DateOnly? start = null, DateOnly? resigned = null, int? workshop = null, params string[] groups)
    {
        var employee = new Employee
        {
            EmployeeId = code, FullName = $"Name {code}", CurrentWorkshopId = workshop ?? workshopId, Shift = shift, Process = "Pack",
            StartDate = start ?? new DateOnly(2025, 1, 6), ResignedDate = resigned,
            EmploymentState = resigned is null ? EmploymentState.Active : EmploymentState.Resigned,
            TelephoneNumber = "0812345678", Accommodation = "Dorm 1", Remark = "private"
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

    void Keep(Employee employee, int day, LeaveType leave, decimal hours, decimal leaveHours, string shift = "A")
    {
        db.AttendanceEntries.Add(new AttendanceEntry { BusinessDate = new DateOnly(2026, 3, day), Shift = shift, EmployeeId = employee.Id, WorkshopId = workshopId, WorkingHours = hours, LeaveType = leave, LeaveHours = leaveHours, IsConfirmed = true });
        db.SaveChanges();
    }

    async Task<ReportService.AttendanceRow> RowAsync(string code, int? workshop = null, string? shift = null) =>
        (await service.AttendanceMonthAsync(Month, workshop, shift)).Rows.Single(r => r.Code == code);

    [Fact]
    public async Task Attendance_defaults_to_the_standard_hours_and_marks_off_days()
    {
        AddEmployee("E1");
        var table = await service.AttendanceMonthAsync(Month, null, null);
        var row = table.Rows.Single();
        Assert.Equal(31, table.Days.Count);
        Assert.Equal(31, row.Cells.Count);
        Assert.Equal("10.67", row.Cells[0]);
        Assert.Equal("OFF", row.Cells[2]);
        Assert.Equal(30 * 10.67m, row.WorkedHours);
        Assert.Equal(0m, row.LeaveHours);
    }

    [Fact]
    public async Task Days_that_have_not_come_yet_hide_their_hours_but_keep_leave_and_remarks()
    {
        var employee = AddEmployee("E1");
        // A month far in the future is entirely ahead of the current business day.
        for (var d = new DateOnly(2099, 1, 1); d.Month == 1; d = d.AddDays(1)) db.WorkCalendars.Add(new WorkCalendar { WorkshopId = workshopId, WorkDate = d, IsWorkingDay = true });
        db.AttendanceEntries.Add(new AttendanceEntry { BusinessDate = new DateOnly(2099, 1, 5), Shift = "A", EmployeeId = employee.Id, WorkshopId = workshopId, WorkingHours = 0, LeaveType = LeaveType.AnnualLeave, LeaveHours = 10.67m, IsConfirmed = true });
        db.AttendanceEntries.Add(new AttendanceEntry { BusinessDate = new DateOnly(2099, 1, 7), Shift = "A", EmployeeId = employee.Id, WorkshopId = workshopId, WorkingHours = 8, LeaveType = LeaveType.None, LeaveHours = 0, IsConfirmed = true });
        db.AttendanceNotes.Add(new AttendanceNote { BusinessDate = new DateOnly(2099, 1, 6), EmployeeId = employee.Id, Text = "planned" });
        db.SaveChanges();
        var row = (await service.AttendanceMonthAsync("2099-01", null, null)).Rows.Single();
        Assert.Equal("", row.Cells[0]);
        Assert.Equal("AL", row.Cells[4]);
        Assert.Equal("", row.Cells[5]);
        Assert.Equal("", row.Cells[6]); // 8 hours entered by hand is hidden too
        Assert.Equal("planned", row.DayNotes![5]);
        Assert.Equal(10.67m, row.LeaveHours);
        Assert.Equal(0m, row.WorkedHours);
    }

    [Fact]
    public async Task Worked_hours_are_summed_per_Monday_to_Sunday_week_cut_at_the_month_edges()
    {
        var employee = AddEmployee("E1");
        Keep(employee, 4, LeaveType.AnnualLeave, 0, 10.67m);
        var table = await service.AttendanceMonthAsync(Month, null, null);
        // 1 March 2026 is a Sunday: 1 | 2-8 | 9-15 | 16-22 | 23-29 | 30-31.
        Assert.Equal(["01/03-01/03", "02/03-08/03", "09/03-15/03", "16/03-22/03", "23/03-29/03", "30/03-31/03"], table.Weeks!.Select(w => w.Label));
        var row = table.Rows.Single();
        // Week 2 has an OFF day and a full-day leave, so 5 of its 7 days carry hours.
        Assert.Equal([10.67m, 5 * 10.67m, 7 * 10.67m, 7 * 10.67m, 7 * 10.67m, 2 * 10.67m], row.WeekHours);
        Assert.Equal(row.WorkedHours, row.WeekHours!.Sum());
    }

    [Fact]
    public async Task Leave_trend_counts_people_per_leave_type_against_all_manpower_without_the_Day_shift()
    {
        var one = AddEmployee("E1");
        var two = AddEmployee("E2");
        var day = AddEmployee("D1", shift: "Day");
        Keep(one, 4, LeaveType.Absent, 0, 10.67m);
        Keep(one, 5, LeaveType.SickLeave, 6.67m, 4m);
        Keep(two, 4, LeaveType.AnnualLeave, 0, 10.67m);
        Keep(day, 4, LeaveType.AnnualLeave, 0, 10.67m, "Day");
        var trend = await service.LeaveTrendAsync(Month, null, null);
        Assert.All(trend.Rows, r => Assert.Equal(("PSL", "A", 2), (r.Workshop, r.Shift, r.Manpower)));
        Assert.Equal(31, trend.Rows.Count);
        var fourth = trend.Rows.Single(r => r.Date.Day == 4);
        Assert.Equal((1, 1, 0, 2), (fourth.Of(LeaveType.AnnualLeave), fourth.Of(LeaveType.Absent), fourth.Of(LeaveType.SickLeave), fourth.Total));
        Assert.Equal(1, fourth.TotalWithoutAl); // AL + AB
        Assert.Equal(1, trend.Rows.Single(r => r.Date.Day == 5).Of(LeaveType.SickLeave));
        Assert.Equal(1, trend.Rows.Single(r => r.Date.Day == 5).TotalWithoutAl);
        var al = trend.Series(r => r.Of(LeaveType.AnnualLeave));
        Assert.Equal(0.5m, al.Single(p => p.Date.Day == 4).Ratio);
        Assert.Equal(0.5m / 31, al.Average(p => p.Ratio));
        Assert.Empty((await service.LeaveTrendAsync(Month, null, "Day")).Rows);
    }

    [Fact]
    public async Task Leave_trend_does_not_count_people_before_they_start_or_after_they_resign()
    {
        AddEmployee("OUT", resigned: new DateOnly(2026, 3, 10));
        AddEmployee("NEW", start: new DateOnly(2026, 3, 5));
        var trend = await service.LeaveTrendAsync(Month, null, null);
        Assert.Equal(1, trend.Rows.Single(r => r.Date.Day == 4).Manpower);
        Assert.Equal(2, trend.Rows.Single(r => r.Date.Day == 7).Manpower);
        Assert.Equal(1, trend.Rows.Single(r => r.Date.Day == 11).Manpower);
    }

    [Fact]
    public async Task Keyed_leave_shows_its_code_and_hours()
    {
        var employee = AddEmployee("E1");
        Keep(employee, 4, LeaveType.AnnualLeave, 0, 10.67m);
        Keep(employee, 5, LeaveType.SickLeave, 6.67m, 4m);
        var row = await RowAsync("E1");
        Assert.Equal("AL", row.Cells[3]);
        Assert.Equal("SL4", row.Cells[4]);
        Assert.Equal(10.67m + 4m, row.LeaveHours);
        Assert.Equal(28 * 10.67m + 6.67m, row.WorkedHours);
    }

    [Fact]
    public async Task A_staged_change_overrides_the_stored_entry_and_a_revert_restores_the_default()
    {
        var employee = AddEmployee("E1");
        Keep(employee, 4, LeaveType.AnnualLeave, 0, 10.67m);
        Keep(employee, 5, LeaveType.AnnualLeave, 0, 10.67m);
        store.StageCells([
            new StagedCell { EmployeeId = employee.Id, Date = new DateOnly(2026, 3, 4), Shift = "A", WorkshopId = workshopId, Hours = 12m },
            new StagedCell { EmployeeId = employee.Id, Date = new DateOnly(2026, 3, 5), Shift = "A", WorkshopId = workshopId, Revert = true }]);
        var row = await RowAsync("E1");
        Assert.Equal("12.00", row.Cells[3]);
        Assert.Equal("10.67", row.Cells[4]);
        Assert.Equal(0m, row.LeaveHours);
    }

    [Fact]
    public async Task Days_after_the_last_working_day_are_resigned_and_days_before_the_start_are_blank()
    {
        AddEmployee("OUT", resigned: new DateOnly(2026, 3, 10));
        AddEmployee("NEW", start: new DateOnly(2026, 3, 5));
        var resigned = await RowAsync("OUT");
        Assert.Equal("10.67", resigned.Cells[9]);
        Assert.Equal(ReportService.Resigned, resigned.Cells[10]);
        var hired = await RowAsync("NEW");
        Assert.Equal("", hired.Cells[3]);
        Assert.Equal("10.67", hired.Cells[4]);
    }

    [Fact]
    public async Task Filters_by_workshop_and_shift_and_leaves_out_the_pregnant_group()
    {
        var other = AddWorkshop("PSLB");
        AddEmployee("A1"); AddEmployee("B1", shift: "B"); AddEmployee("X1", workshop: other);
        AddEmployee("PRG", groups: [PregnantModel.GroupName]);
        var everyone = (await service.AttendanceMonthAsync(Month, null, null)).Rows.Select(r => r.Code).Order().ToList();
        Assert.Equal(["A1", "B1", "X1"], everyone);
        Assert.Equal(["A1", "B1"], (await service.AttendanceMonthAsync(Month, workshopId, null)).Rows.Select(r => r.Code).Order());
        Assert.Equal(["B1"], (await service.AttendanceMonthAsync(Month, workshopId, "B")).Rows.Select(r => r.Code));
    }

    [Fact]
    public async Task Filters_attendance_by_special_group_and_lists_pregnant_only_when_picked()
    {
        AddEmployee("N1", groups: ["Night"]); AddEmployee("N2", shift: "B", groups: ["Night", "Student"]); AddEmployee("S1", groups: ["Student"]);
        var prg = AddEmployee("PRG", groups: [PregnantModel.GroupName]); AddEmployee("PLAIN");
        var night = db.SpecialGroups.Single(g => g.Name == "Night").Id;
        Assert.Equal(["N1", "N2"], (await service.AttendanceMonthAsync(Month, null, null, specialGroupId: night)).Rows.Select(r => r.Code).Order());
        Assert.Equal(["N2"], (await service.AttendanceMonthAsync(Month, null, "B", specialGroupId: night)).Rows.Select(r => r.Code));
        Assert.Equal(4, (await service.AttendanceMonthAsync(Month, null, null)).Rows.Count);
        Assert.Equal(["Night", "Pregnant", "Student"], (await service.SpecialGroupsAsync()).Select(g => g.Name));

        // Pregnant: own entries (shift key "Pregnant"), 8 standard hours, Monday to Friday.
        db.AttendanceEntries.Add(new AttendanceEntry { BusinessDate = new DateOnly(2026, 3, 4), Shift = PregnantModel.ShiftKey, EmployeeId = prg.Id, WorkshopId = workshopId, WorkingHours = 0, LeaveType = LeaveType.MotherLeave, LeaveHours = 8m, IsConfirmed = true });
        db.SaveChanges();
        var pregnant = db.SpecialGroups.Single(g => g.Name == PregnantModel.GroupName).Id;
        var row = (await service.AttendanceMonthAsync(Month, null, null, specialGroupId: pregnant)).Rows.Single();
        Assert.Equal("PRG", row.Code);
        Assert.Equal("8", row.Cells[1].Replace(".00", ""));   // Mon 2 Mar
        Assert.Equal("OFF", row.Cells[6]);                    // Sat 7 Mar
        Assert.StartsWith("ML", row.Cells[3]);                // Wed 4 Mar
    }

    [Fact]
    public async Task Attendance_and_working_hours_leave_out_the_Day_shift_but_the_employee_list_keeps_it()
    {
        AddEmployee("A1"); AddEmployee("D1", shift: "Day");
        Assert.Equal(["A1"], (await service.AttendanceMonthAsync(Month, null, null)).Rows.Select(r => r.Code));
        Assert.Empty((await service.AttendanceMonthAsync(Month, null, "Day")).Rows);
        var day = new DateOnly(2026, 3, 2);
        db.ShiftManpowers.Add(new ShiftManpower { BusinessDate = day, WorkshopId = workshopId, Shift = "A", TotalHours = 100m, IndirectHours = 30m });
        db.ShiftManpowers.Add(new ShiftManpower { BusinessDate = day, WorkshopId = workshopId, Shift = "Day", TotalHours = 80m, IndirectHours = 10m });
        db.SaveChanges();
        Assert.Equal(["A"], (await service.WorkingHoursAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, null)).Select(r => r.Shift));
        Assert.Empty(await service.WorkingHoursAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, "Day"));
        Assert.Equal(["A1", "D1"], (await service.EmployeesAsync(null, null, null)).Select(r => r.Code));
    }

    [Fact]
    public async Task Remarks_come_from_storage_unless_a_staged_change_replaces_or_clears_them()
    {
        var employee = AddEmployee("E1");
        db.AttendanceNotes.Add(new AttendanceNote { EmployeeId = employee.Id, BusinessDate = new DateOnly(2026, 3, 4), Text = "stored" });
        db.AttendanceNotes.Add(new AttendanceNote { EmployeeId = employee.Id, BusinessDate = new DateOnly(2026, 3, 5), Text = "to clear" });
        db.SaveChanges();
        store.StageNotes([
            new StagedNote { EmployeeId = employee.Id, Date = new DateOnly(2026, 3, 4), Text = "staged" },
            new StagedNote { EmployeeId = employee.Id, Date = new DateOnly(2026, 3, 5), Text = "" }]);
        Assert.Equal("04/03: staged", (await RowAsync("E1")).Remarks);
    }

    [Fact]
    public async Task Working_hours_list_only_closed_days_and_direct_is_total_minus_indirect()
    {
        var day = new DateOnly(2026, 3, 2);
        db.ShiftManpowers.Add(new ShiftManpower { BusinessDate = day, WorkshopId = workshopId, Shift = "A", TotalHours = 100m, IndirectHours = 30m, DirectCount = 7, IndirectCount = 3 });
        db.ShiftManpowers.Add(new ShiftManpower { BusinessDate = day, WorkshopId = workshopId, Shift = "B", TotalHours = null });
        db.ShiftManpowers.Add(new ShiftManpower { BusinessDate = day.AddDays(40), WorkshopId = workshopId, Shift = "A", TotalHours = 50m, IndirectHours = 0m });
        db.SaveChanges();
        var rows = await service.WorkingHoursAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, null);
        var only = Assert.Single(rows);
        Assert.Equal(("PSL", "A", 100m, 30m, 70m), (only.Workshop, only.Shift, only.Total, only.Indirect, only.Direct));
        Assert.Equal((10, 3, 7), (only.TotalCount, only.IndirectCount, only.DirectCount));
        Assert.Empty(await service.WorkingHoursAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, "C"));
    }

    [Fact]
    public async Task Employee_list_filters_by_status_and_carries_groups()
    {
        AddEmployee("ACT", groups: ["Trainer", "Support"]);
        AddEmployee("OUT", resigned: new DateOnly(2026, 2, 1));
        Assert.Equal(["ACT", "OUT"], (await service.EmployeesAsync(null, null, null)).Select(r => r.Code));
        Assert.Equal(["ACT"], (await service.EmployeesAsync(null, null, "Active")).Select(r => r.Code));
        var resigned = Assert.Single(await service.EmployeesAsync(null, null, "Resigned"));
        Assert.Equal(new DateOnly(2026, 2, 1), resigned.ResignedDate);
        Assert.Equal("Support; Trainer", (await service.EmployeesAsync(null, null, "Active")).Single().Groups);
    }

    [Fact]
    public async Task Excel_files_open_and_hold_the_data()
    {
        var employee = AddEmployee("E1");
        Keep(employee, 4, LeaveType.AnnualLeave, 0, 10.67m);
        db.ShiftManpowers.Add(new ShiftManpower { BusinessDate = new DateOnly(2026, 3, 2), WorkshopId = workshopId, Shift = "A", TotalHours = 100m, IndirectHours = 30m, DirectCount = 7, IndirectCount = 3 });
        db.SaveChanges();

        using (var book = new XLWorkbook(new MemoryStream(ReportExcel.Attendance(await service.AttendanceMonthAsync(Month, null, null)))))
        {
            var sheet = book.Worksheet(1);
            Assert.Equal("Emp ID", sheet.Cell(1, 1).GetString());
            Assert.Equal("E1", sheet.Cell(2, 1).GetString());
            Assert.Equal(10.67m, sheet.Cell(2, 6).GetValue<decimal>());
            Assert.Equal("OFF", sheet.Cell(2, 8).GetString());
            Assert.Equal("AL", sheet.Cell(2, 9).GetString());
        }
        using (var book = new XLWorkbook(new MemoryStream(ReportExcel.LeaveTrend(await service.LeaveTrendAsync(Month, null, null)))))
        {
            var sheet = book.Worksheet(1);
            Assert.Equal(["Date", "Workshop", "Shift", "Manpower", "AL", "SL", "BL", "OL", "AB", "ML", "Total leave (incl. AL)", "Total leave (excl. AL)",
                "%AL", "%SL", "%BL", "%OL", "%AB", "%ML", "%Total Leave (incl. AL)", "%Total Leave (excl. AL)"], Enumerable.Range(1, 20).Select(c => sheet.Cell(1, c).GetString()));
            Assert.Equal(("PSL", "A", 1, 1), (sheet.Cell(5, 2).GetString(), sheet.Cell(5, 3).GetString(), sheet.Cell(5, 4).GetValue<int>(), sheet.Cell(5, 5).GetValue<int>()));
            // 4 March: the only person is on AL, so everything is AL and nothing is left once AL is excluded.
            Assert.Equal((1, 0, 1d, 0d, 1d, 0d), (sheet.Cell(5, 11).GetValue<int>(), sheet.Cell(5, 12).GetValue<int>(), sheet.Cell(5, 13).GetValue<double>(),
                sheet.Cell(5, 14).GetValue<double>(), sheet.Cell(5, 19).GetValue<double>(), sheet.Cell(5, 20).GetValue<double>()));
        }
        using (var book = new XLWorkbook(new MemoryStream(ReportExcel.WorkingHours(await service.WorkingHoursAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, null)))))
        {
            var sheet = book.Worksheet(1);
            Assert.Equal(70m, sheet.Cell(2, 6).GetValue<decimal>());
            Assert.Equal(["Total headcount", "Indirect headcount", "Direct headcount"], Enumerable.Range(7, 3).Select(c => sheet.Cell(1, c).GetString()));
            Assert.Equal((10, 3, 7), (sheet.Cell(2, 7).GetValue<int>(), sheet.Cell(2, 8).GetValue<int>(), sheet.Cell(2, 9).GetValue<int>()));
        }
        using (var book = new XLWorkbook(new MemoryStream(ReportExcel.Employees(await service.EmployeesAsync(null, null, null)))))
        {
            var sheet = book.Worksheet(1);
            Assert.Equal("Name E1", sheet.Cell(2, 2).GetString());
            Assert.DoesNotContain(sheet.CellsUsed(), c => c.GetString().Contains("0812345678") || c.GetString() == "Dorm 1" || c.GetString() == "private");
        }
    }
}
