using Xunit;
using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManpowerManagement.Tests;

// Runs against an in-memory database: the real SQL Server holds production data and must not be used for tests.
public class HourCountingTests : IDisposable
{
    // A Monday well in the past, so the business day is always closed.
    static readonly DateOnly Day = new(2026, 3, 2);
    readonly string stagingPath = Path.Combine(Path.GetTempPath(), $"staging-{Guid.NewGuid():N}.json");
    readonly AppDbContext db;
    readonly AttendanceStagingStore store;
    readonly int workshopId;

    public HourCountingTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        store = new AttendanceStagingStore(stagingPath, NullLogger<AttendanceStagingStore>.Instance);
        var workshop = new Workshop { Name = "PSL" };
        db.Workshops.Add(workshop);
        db.SaveChanges();
        workshopId = workshop.Id;
        // Every test day is a working day for the workshop, whatever the shift cycle says.
        for (var d = Day.AddDays(-3); d <= Day.AddDays(8); d = d.AddDays(1))
            db.WorkCalendars.Add(new WorkCalendar { WorkshopId = workshopId, WorkDate = d, IsWorkingDay = true });
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(stagingPath) + "*")) File.Delete(file);
    }

    Employee AddEmployee(string code, string shift = "A", string grade = "50T", DateOnly? start = null)
    {
        var employee = new Employee { EmployeeId = code, FullName = code, CurrentWorkshopId = workshopId, Shift = shift, JobGrade = grade, StartDate = start ?? Day.AddYears(-1) };
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    DailyHoursService Hours() => new(db, new EffectiveAttendance(db));
    AttendanceFlushService Flusher() => new(db, Hours(), store, NullLogger<AttendanceFlushService>.Instance);
    ShiftManpower Summary(string shift) => db.ShiftManpowers.AsNoTracking().Single(m => m.BusinessDate == Day && m.WorkshopId == workshopId && m.Shift == shift);

    [Fact]
    public async Task Everyone_counts_the_standard_hours_when_nothing_is_keyed()
    {
        for (var i = 0; i < 20; i++) AddEmployee($"E{i}");
        await Hours().FinalizeDayAsync(Day);
        Assert.Equal(20 * 10.67m, Summary("A").TotalHours);
    }

    [Fact]
    public async Task Keyed_leave_reduces_the_total()
    {
        var annual = AddEmployee("AL"); var sick = AddEmployee("SL"); AddEmployee("OK");
        db.AttendanceEntries.Add(new AttendanceEntry { BusinessDate = Day, Shift = "A", EmployeeId = annual.Id, WorkshopId = workshopId, WorkingHours = 0, LeaveType = LeaveType.AnnualLeave, LeaveHours = 10.67m, IsConfirmed = true });
        db.AttendanceEntries.Add(new AttendanceEntry { BusinessDate = Day, Shift = "A", EmployeeId = sick.Id, WorkshopId = workshopId, WorkingHours = 6.67m, LeaveType = LeaveType.SickLeave, LeaveHours = 4m, IsConfirmed = true });
        db.SaveChanges();
        await Hours().FinalizeDayAsync(Day);
        Assert.Equal(0m + 6.67m + 10.67m, Summary("A").TotalHours);
    }

    [Fact]
    public async Task An_off_day_creates_no_summary_and_counts_only_overtime()
    {
        var worker = AddEmployee("OT"); AddEmployee("REST");
        var off = Day.AddDays(1);
        db.WorkCalendars.Single(c => c.WorkDate == off).IsWorkingDay = false;
        db.AttendanceEntries.Add(new AttendanceEntry { BusinessDate = off, Shift = "A", EmployeeId = worker.Id, WorkshopId = workshopId, WorkingHours = 4m, IsConfirmed = true });
        db.SaveChanges();
        await Hours().FinalizeDayAsync(off);
        Assert.Equal(4m, db.ShiftManpowers.Single(m => m.BusinessDate == off).TotalHours);
    }

    [Fact]
    public async Task Employees_who_have_not_started_or_have_resigned_are_not_counted()
    {
        AddEmployee("NOW"); AddEmployee("LATER", start: Day.AddDays(1));
        var gone = AddEmployee("GONE"); gone.ResignedDate = Day.AddDays(-1); db.SaveChanges();
        await Hours().FinalizeDayAsync(Day);
        Assert.Equal(10.67m, Summary("A").TotalHours);
    }

    [Fact]
    public async Task Leaders_below_56T_count_half_and_leaders_from_56T_are_indirect()
    {
        var low = AddEmployee("LOW", grade: "52T"); var high = AddEmployee("HIGH", grade: "58T"); AddEmployee("ST");
        db.AttendanceLeaders.Add(new AttendanceLeader { EmployeeCode = "LOW", WorkshopId = workshopId, IsActive = true });
        db.AttendanceLeaders.Add(new AttendanceLeader { EmployeeCode = "HIGH", WorkshopId = workshopId, IsActive = true });
        db.ShiftManpowers.Add(new ShiftManpower { BusinessDate = Day, WorkshopId = workshopId, Shift = "A", Indirect1067Count = 1, EnteredBy = "x" });
        db.SaveChanges();
        await Hours().FinalizeDayAsync(Day);
        var summary = Summary("A");
        Assert.Equal(10.67m * 0.5m + 10.67m + 10.67m, summary.TotalHours);
        // One keyed 10.67 headcount plus the 58T leader's full hours.
        Assert.Equal(10.67m + 10.67m, summary.IndirectHours);
        Assert.Equal(1 + 2, summary.IndirectCount);
        Assert.Equal(3 - 3, summary.DirectCount);
    }

    [Fact]
    public async Task Day_shift_is_entirely_indirect_without_the_half_count()
    {
        AddEmployee("D1", "Day", "50T"); AddEmployee("D2", "Day", "50T");
        db.AttendanceLeaders.Add(new AttendanceLeader { EmployeeCode = "D1", WorkshopId = workshopId, IsActive = true });
        db.SaveChanges();
        await Hours().FinalizeDayAsync(Day);
        var summary = Summary("Day");
        Assert.Equal(2 * 10.67m, summary.TotalHours);
        Assert.Equal(summary.TotalHours, summary.IndirectHours);
    }

    [Fact]
    public async Task Pregnant_employees_count_8_hours_on_weekdays_into_their_roster_shift()
    {
        var mother = AddEmployee("MUM"); AddEmployee("ST");
        var group = new SpecialGroup { Name = "Pregnant" }; db.SpecialGroups.Add(group); db.SaveChanges();
        db.EmployeeSpecialGroups.Add(new EmployeeSpecialGroup { EmployeeId = mother.Id, SpecialGroupId = group.Id }); db.SaveChanges();
        await Hours().FinalizeDayAsync(Day);
        Assert.Equal(10.67m + 8m, Summary("A").TotalHours);
    }

    [Fact]
    public async Task A_manual_indirect_correction_survives_a_refresh()
    {
        AddEmployee("E1");
        await Hours().FinalizeDayAsync(Day);
        var row = db.ShiftManpowers.Single(); row.IndirectHours = 3m; row.IndirectHoursIsManual = true; db.SaveChanges();
        await Hours().RefreshAsync(Day, Day);
        Assert.Equal(3m, Summary("A").IndirectHours);
    }

    [Fact]
    public async Task Flush_writes_staged_changes_to_sql_and_empties_the_staging_file()
    {
        var employee = AddEmployee("E1"); AddEmployee("E2");
        store.StageCells([new StagedCell { EmployeeId = employee.Id, Date = Day, Shift = "A", WorkshopId = workshopId, Hours = 0, LeaveType = (int)LeaveType.AnnualLeave, LeaveHours = 10.67m, Actor = "leader" }]);
        store.StageManpower([new StagedManpower { WorkshopId = workshopId, Shift = "A", Date = Day, I1067 = 1, Actor = "leader" }]);
        Assert.Empty(db.AttendanceEntries);

        await Flusher().FlushAsync();

        Assert.Equal(LeaveType.AnnualLeave, db.AttendanceEntries.Single().LeaveType);
        Assert.True(db.AttendanceEntries.Single().IsConfirmed);
        Assert.Equal(1, db.ShiftManpowers.Single().Indirect1067Count);
        Assert.Equal(0, store.PendingCount);
        Assert.Contains("\"cells\": []", File.ReadAllText(stagingPath));
    }

    [Fact]
    public async Task Reverting_a_cell_removes_the_stored_entry()
    {
        var employee = AddEmployee("E1");
        db.AttendanceEntries.Add(new AttendanceEntry { BusinessDate = Day, Shift = "A", EmployeeId = employee.Id, WorkshopId = workshopId, WorkingHours = 0, LeaveType = LeaveType.Absent, LeaveHours = 10.67m, IsConfirmed = true });
        db.SaveChanges();
        store.StageCells([new StagedCell { EmployeeId = employee.Id, Date = Day, Shift = "A", WorkshopId = workshopId, Revert = true, Actor = "leader" }]);
        await Flusher().FlushAsync();
        Assert.Empty(db.AttendanceEntries);
    }

    [Fact]
    public async Task A_change_made_during_a_flush_stays_staged()
    {
        var employee = AddEmployee("E1");
        store.StageCells([new StagedCell { EmployeeId = employee.Id, Date = Day, Shift = "A", WorkshopId = workshopId, Hours = 5, Actor = "a" }]);
        var snapshot = store.Snapshot();
        store.StageCells([new StagedCell { EmployeeId = employee.Id, Date = Day, Shift = "A", WorkshopId = workshopId, Hours = 7, Actor = "b" }]);
        store.Remove(snapshot);
        var left = Assert.Single(store.PendingCells(_ => true));
        Assert.Equal(7m, left.Hours);
        await Task.CompletedTask;
    }

    [Fact]
    public void Staged_changes_survive_a_restart()
    {
        store.StageCells([new StagedCell { EmployeeId = 1, Date = Day, Shift = "A", WorkshopId = 1, Hours = 5, Actor = "a" }]);
        store.StageManpower([new StagedManpower { WorkshopId = 1, Shift = "A", Date = Day, I841 = 2, Actor = "a" }]);
        var reloaded = new AttendanceStagingStore(stagingPath, NullLogger<AttendanceStagingStore>.Instance);
        Assert.Equal(2, reloaded.PendingCount);
        Assert.Equal(2, reloaded.PendingManpower(_ => true).Single().I841);
    }

    [Fact]
    public async Task Flush_writes_a_staged_remark_to_sql_and_an_empty_one_deletes_it()
    {
        var employee = AddEmployee("E1");
        store.StageNotes([new StagedNote { EmployeeId = employee.Id, Date = Day, Text = "Booked annual leave", Actor = "leader" }]);
        Assert.Equal(1, store.PendingCount);

        await Flusher().FlushAsync();

        var saved = db.AttendanceNotes.Single();
        Assert.Equal("Booked annual leave", saved.Text);
        Assert.Equal("leader", saved.UpdatedBy);
        Assert.Equal(0, store.PendingCount);
        // A remark never creates an attendance entry, so the day still counts as a default one.
        Assert.Empty(db.AttendanceEntries);

        store.StageNotes([new StagedNote { EmployeeId = employee.Id, Date = Day, Text = "Moved to Pellet Ass'y", Actor = "leader" }]);
        await Flusher().FlushAsync();
        Assert.Equal("Moved to Pellet Ass'y", db.AttendanceNotes.Single().Text);

        store.StageNotes([new StagedNote { EmployeeId = employee.Id, Date = Day, Text = "", Actor = "leader" }]);
        await Flusher().FlushAsync();
        Assert.Empty(db.AttendanceNotes);
    }

    [Fact]
    public async Task A_remark_survives_putting_the_cell_back_to_default()
    {
        var employee = AddEmployee("E1");
        db.AttendanceEntries.Add(new AttendanceEntry { BusinessDate = Day, Shift = "A", EmployeeId = employee.Id, WorkshopId = workshopId, WorkingHours = 0, LeaveType = LeaveType.Absent, LeaveHours = 10.67m, IsConfirmed = true });
        db.AttendanceNotes.Add(new AttendanceNote { EmployeeId = employee.Id, BusinessDate = Day, Text = "No call", UpdatedBy = "leader" });
        db.SaveChanges();
        store.StageCells([new StagedCell { EmployeeId = employee.Id, Date = Day, Shift = "A", WorkshopId = workshopId, Revert = true, Actor = "leader" }]);
        await Flusher().FlushAsync();
        Assert.Empty(db.AttendanceEntries);
        Assert.Equal("No call", db.AttendanceNotes.Single().Text);
    }

    [Fact]
    public void A_remark_edited_during_a_flush_stays_staged()
    {
        store.StageNotes([new StagedNote { EmployeeId = 1, Date = Day, Text = "first", Actor = "a" }]);
        var snapshot = store.Snapshot();
        store.StageNotes([new StagedNote { EmployeeId = 1, Date = Day, Text = "second", Actor = "b" }]);
        store.Remove(snapshot);
        Assert.Equal("second", Assert.Single(store.PendingNotes(_ => true)).Text);
    }

    [Fact]
    public void Staged_remarks_survive_a_restart_and_an_old_staging_file_still_loads()
    {
        store.StageNotes([new StagedNote { EmployeeId = 1, Date = Day, Text = "OT pellet", Actor = "a" }]);
        var reloaded = new AttendanceStagingStore(stagingPath, NullLogger<AttendanceStagingStore>.Instance);
        Assert.Equal(1, reloaded.PendingCount);
        Assert.Equal("OT pellet", reloaded.PendingNotes(_ => true).Single().Text);

        File.WriteAllText(stagingPath, "{ \"nextVersion\": 5, \"cells\": [], \"manpower\": [] }");
        Assert.Equal(0, new AttendanceStagingStore(stagingPath, NullLogger<AttendanceStagingStore>.Instance).PendingCount);
    }

    [Fact]
    public async Task The_morning_run_flushes_then_closes_yesterday_and_does_not_repeat()
    {
        var employee = AddEmployee("E1"); AddEmployee("E2");
        store.StageCells([new StagedCell { EmployeeId = employee.Id, Date = Day, Shift = "A", WorkshopId = workshopId, Hours = 0, LeaveType = (int)LeaveType.SickLeave, LeaveHours = 10.67m, Actor = "leader" }]);
        var now = Day.AddDays(1).ToDateTime(new TimeOnly(7, 2));

        Assert.True(await Flusher().RunIfDueAsync(now));
        Assert.Equal(10.67m, Summary("A").TotalHours);
        Assert.Equal(Day, store.LastFinalizedDay);
        Assert.False(await Flusher().RunIfDueAsync(now.AddMinutes(5)));
        Assert.True(await Flusher().RunIfDueAsync(Day.AddDays(1).ToDateTime(new TimeOnly(19, 1))));
    }

    [Fact]
    public void Readers_see_pending_changes_before_the_flush()
    {
        var stored = new List<AttendanceEntry>
        {
            new() { EmployeeId = 1, BusinessDate = Day, Shift = "A", WorkshopId = workshopId, LeaveType = LeaveType.Absent, LeaveHours = 10.67m, IsConfirmed = true },
            new() { EmployeeId = 2, BusinessDate = Day, Shift = "A", WorkshopId = workshopId, LeaveType = LeaveType.SickLeave, LeaveHours = 4m, WorkingHours = 6.67m, IsConfirmed = true }
        };
        store.StageCells([
            new StagedCell { EmployeeId = 1, Date = Day, Shift = "A", WorkshopId = workshopId, Revert = true },
            new StagedCell { EmployeeId = 3, Date = Day, Shift = "A", WorkshopId = workshopId, LeaveType = (int)LeaveType.AnnualLeave, LeaveHours = 10.67m },
            new StagedCell { EmployeeId = 4, Date = Day, Shift = "A", WorkshopId = 99, LeaveType = (int)LeaveType.AnnualLeave, LeaveHours = 10.67m }]);

        var merged = store.Overlay(stored, Day, Day, c => c.WorkshopId == workshopId);

        Assert.DoesNotContain(merged, e => e.EmployeeId == 1);
        Assert.Contains(merged, e => e.EmployeeId == 2);
        Assert.Contains(merged, e => e.EmployeeId == 3 && e.LeaveType == LeaveType.AnnualLeave);
        Assert.DoesNotContain(merged, e => e.EmployeeId == 4);
    }

    [Theory]
    [InlineData(6, 59, 19, 0)] // before the morning slot: yesterday evening
    [InlineData(7, 0, 7, 0)]
    [InlineData(18, 59, 7, 0)]
    [InlineData(19, 0, 19, 0)]
    public void The_latest_slot_is_07_00_or_19_00(int hour, int minute, int slotHour, int slotMinute)
    {
        var now = new DateTime(2026, 3, 5, hour, minute, 0);
        var slot = AttendanceFlushService.LatestSlot(now);
        Assert.Equal(slotHour, slot.Hour);
        Assert.Equal(slotMinute, slot.Minute);
        Assert.True(slot <= now);
    }
}
