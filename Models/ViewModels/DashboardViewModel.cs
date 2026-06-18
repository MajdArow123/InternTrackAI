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

    // Alerts
    // Applications with a Deadline within the next 3 days.
    public List<JobApplication> UpcomingDeadlines { get; set; } = new();

    // Applications sitting 7+ days without a status change — surfaced as a nudge to follow up.
    public List<JobApplication> FollowUpSuggestions { get; set; } = new();
}
