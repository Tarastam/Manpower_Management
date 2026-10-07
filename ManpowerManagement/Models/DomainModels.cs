using Microsoft.AspNetCore.Identity;

namespace ManpowerManagement.Models;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Approver = "ResignationApprover";
}

public enum EmploymentType { NewComer, Permanent }
public enum EmploymentState { Active, Resigned }
public enum LeaveType { None, AnnualLeave, SickLeave, BusinessLeave, OtherLeave, Absent, MotherLeave }
public enum TicketStatus { Submitted, Approved, Rejected }

public class ApplicationUser : IdentityUser { }

public class Workshop
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public Workshop? Parent { get; set; }
    public ICollection<Workshop> Children { get; set; } = new List<Workshop>();
}

public class Employee
{
    public int Id { get; set; }
    public string EmployeeId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string JobGrade { get; set; } = string.Empty;
    public string Process { get; set; } = string.Empty;
    public string Accommodation { get; set; } = string.Empty;
    public string TelephoneNumber { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public EmploymentType EmploymentType { get; set; }
    // Ticked on purpose when the employee is added: a genuinely new hire, as opposed to someone who was forgotten
    // in the system or transferred from another department. Only tagged people are monitored as new hires.
    public bool IsNewEmployee { get; set; }
    public EmploymentState EmploymentState { get; set; } = EmploymentState.Active;
    public DateOnly? ResignedDate { get; set; }
    public int CurrentWorkshopId { get; set; }
    public Workshop? CurrentWorkshop { get; set; }
    public string Shift { get; set; } = "Day";
    public ICollection<EmployeeHistory> History { get; set; } = new List<EmployeeHistory>();
    public ICollection<EmployeeSpecialGroup> SpecialGroups { get; set; } = new List<EmployeeSpecialGroup>();
}

public class SpecialGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#6c757d";
    public bool IsActive { get; set; } = true;
}

public class EmployeeSpecialGroup
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int SpecialGroupId { get; set; }
    public SpecialGroup? SpecialGroup { get; set; }
}

public class EmployeeHistory
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int WorkshopId { get; set; }
    public Workshop? Workshop { get; set; }
    public EmploymentType EmploymentType { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public string ChangedBy { get; set; } = "Supervisor";
}

public class WorkCalendar
{
    public int Id { get; set; }
    public int WorkshopId { get; set; }
    public Workshop? Workshop { get; set; }
    public DateOnly WorkDate { get; set; }
    public bool IsWorkingDay { get; set; }
}

public class AttendanceEntry
{
    public int Id { get; set; }
    public DateOnly BusinessDate { get; set; }
    public string Shift { get; set; } = "A";
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int WorkshopId { get; set; }
    public Workshop? Workshop { get; set; }
    public decimal WorkingHours { get; set; }
    public LeaveType LeaveType { get; set; }
    public decimal LeaveHours { get; set; }
    public bool IsConfirmed { get; set; }
    public string? EnteredBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

// Daily headcount and working-hour summary for a workshop and shift.
public class ShiftManpower
{
    public int Id { get; set; }
    public DateOnly BusinessDate { get; set; }
    public int WorkshopId { get; set; }
    public Workshop? Workshop { get; set; }
    public string Shift { get; set; } = "A";
    public int DirectCount { get; set; }
    public int IndirectCount { get; set; }
    // Indirect headcount split by working-hour band; IndirectCount is their sum.
    public int Indirect1067Count { get; set; }
    public int Indirect841Count { get; set; }
    public int Indirect733Count { get; set; }
    // Daily hour summary. Null means the business day has not been summarized yet.
    public decimal? TotalHours { get; set; }
    public decimal? IndirectHours { get; set; }
    public bool IndirectHoursIsManual { get; set; }
    public DateTime? CalculatedAtUtc { get; set; }
    public string EnteredBy { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

// Admin-maintained list of who may key attendance, and for which workshop and shift.
public class AttendanceLeader
{
    public int Id { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public int WorkshopId { get; set; }
    public Workshop? Workshop { get; set; }
    // Legacy column: leaders are now assigned per workshop and pick the shift on the attendance page.
    public string Shift { get; set; } = "";
    // Leaders do not have to be in the employee master, so their name is kept here.
    public string? LeaderName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class ResignationTicket
{
    public int Id { get; set; }
    public string RequesterEmployeeId { get; set; } = string.Empty;
    public string RequesterShift { get; set; } = "Day";
    public int WorkshopId { get; set; }
    public Workshop? Workshop { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Submitted;
    public DateOnly? EffectiveResignedDate { get; set; }
    public string? DecisionBy { get; set; }
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum ProbationDecision { Passed, Extended, NotPassed }

// A leader's probation decision for a new hire (pass / extend / not accepted), kept as history.
// The decision is due before day 60; HR only acknowledges it.
public class ProbationReview
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public ProbationDecision Decision { get; set; }
    // Only for Extended: the new probation end date, at most StartDate + 119 days.
    public DateOnly? ExtendedUntil { get; set; }
    public string LeaderComment { get; set; } = string.Empty;
    public string DecidedBy { get; set; } = string.Empty;
    public DateTime DecidedAtUtc { get; set; } = DateTime.UtcNow;
    public string? HrAcknowledgedBy { get; set; }
    public DateTime? HrAcknowledgedAtUtc { get; set; }
}

// Daily output (thousand pieces) keyed by a leader for a new hire.
public class NewHireOutput
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly BusinessDate { get; set; }
    public decimal Kps { get; set; }
    public string EnteredBy { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

// A leader's comment about a new hire on one day: a review of a leave day, or a note that the employee was late.
public class NewHireNote
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly BusinessDate { get; set; }
    public string Kind { get; set; } = NewHireNoteKind.Late;
    public string Comment { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class NewHireNoteKind
{
    public const string Leave = "Leave";
    public const string Late = "Late";
}

// Single-row thresholds for the new hire early-warning rules. Id is always 1.
public class NewHireRiskSetting
{
    public int Id { get; set; } = 1;
    public int ConsecutiveAbsentDays { get; set; } = 2;
    public int NoShowDays { get; set; } = 3;
    public decimal LeaveRatePercent { get; set; } = 10m;
    public int LeaveRateWindowDays { get; set; } = 14;
    public int DecisionWarningDay { get; set; } = 50;
}

public class AuditLog
{
    public int Id { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string EntityKey { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string? Detail { get; set; }
}
