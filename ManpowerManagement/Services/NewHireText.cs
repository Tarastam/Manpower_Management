using ManpowerManagement.Models;
using System.Globalization;

namespace ManpowerManagement.Services;

// Labels and CSS classes shared by the new hire pages.
public static class NewHireText
{
    public static string PhaseLabel(NewHireMonitorService.NewHireRow row) => row.Phase switch
    {
        NewHirePhase.NotStarted => $"Starts in {-row.TenureDays} day(s)",
        NewHirePhase.Evaluating => $"Evaluating · decide by {Date(row.Deadline)}",
        NewHirePhase.DecisionSoon => row.DaysToDeadline == 0 ? "Decision due today" : $"Decision due in {row.DaysToDeadline} day(s)",
        NewHirePhase.DecisionOverdue => row.DaysToDeadline == 0 ? "Decision due today" : $"Decision overdue by {-row.DaysToDeadline} day(s)",
        NewHirePhase.Extended => $"Extended to {Date(row.Deadline)}",
        NewHirePhase.Passed => row.Latest?.HrAcknowledgedAtUtc is null ? "Passed · HR to acknowledge" : "Passed",
        _ => row.Latest?.HrAcknowledgedAtUtc is null ? "Not passed · HR to acknowledge" : "Not passed"
    };

    public static string PhaseCss(NewHirePhase phase) => phase switch
    {
        NewHirePhase.DecisionOverdue or NewHirePhase.NotPassed => "nh-badge-red",
        NewHirePhase.DecisionSoon or NewHirePhase.Extended => "nh-badge-amber",
        NewHirePhase.Passed => "nh-badge-green",
        _ => "nh-badge-blue"
    };

    public static string DecisionLabel(ProbationDecision decision) => decision switch
    {
        ProbationDecision.Passed => "Passed probation",
        ProbationDecision.Extended => "Extend probation",
        _ => "Not accepted"
    };

    public static string LeaveLabel(LeaveType type) => type switch
    {
        LeaveType.AnnualLeave => "Annual leave", LeaveType.SickLeave => "Sick leave", LeaveType.BusinessLeave => "Business leave",
        LeaveType.MotherLeave => "Maternity leave", LeaveType.Absent => "Absent", _ => "Other leave"
    };

    public static string Date(DateOnly date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    public static string Pct(decimal? value) => value is null ? "—" : value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
