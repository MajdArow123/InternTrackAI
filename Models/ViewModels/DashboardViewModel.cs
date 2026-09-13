using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Aggregated data for the Dashboard page. All fields are computed server-side from the
/// user's full set of <see cref="JobApplication"/> rows — this view model exists so the
/// dashboard view never has to re-derive stats/alerts itself.
/// </summary>
public class DashboardViewModel
{
    public int TotalApplications { get; set; }

    // Count of applications per pipeline stage, used to render the per-status stat cards.
    public Dictionary<ApplicationStatus, int> StatusCounts { get; set; } = new();

    // Most recent 8 applications (by date), shown in the dashboard's activity table.
    public List<JobApplication> RecentApplications { get; set; } = new();

    // Analytics
    // Offer count divided by total applications — gives the user a quick read on how
    // their search is going.
    public double SuccessRate { get; set; }
    public List<KeyValuePair<string, int>> TopCompanies { get; set; } = new();

    // Applications applied to per month, oldest to newest, for the last 6 months —
    // drives the "applications over time" line chart. Bucketed by DateApplied since
    // there's no separate CreatedAt timestamp on JobApplication.
    public List<KeyValuePair<string, int>> ApplicationsOverTime { get; set; } = new();

    // Attention card: the most urgent reminders (overdue, deadline soon, follow-up due, interview)
    // from ReminderService, capped at AttentionLimit; AttentionTotal is the uncapped count.
    public const int AttentionLimit = 8;
    public List<InternTrackAI.Services.ReminderItem> Attention { get; set; } = new();
    public int AttentionTotal { get; set; }

    // "Resume performance" card: per-resume sent / response rate / interviews / offers / avg match.
    // Rendered only when ResumeAnalytics.ShowCard (two or more resume versions).
    public InternTrackAI.Services.ResumeAnalytics ResumeAnalytics { get; set; } = new();

    // "Inbox suggestions" card: pending StatusSuggestions (with Application loaded), newest email first.
    // Rendered above Attention only when non-empty; absent entirely without a Gmail connection.
    public List<StatusSuggestion> Suggestions { get; set; } = new();

    // Onboarding checklist (shown only to brand-new users with zero applications).
    public bool HasProfileBasics { get; set; }
    public bool HasResume { get; set; }
}
