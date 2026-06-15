using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models.ViewModels;

public class DashboardViewModel
{
    public int TotalApplications { get; set; }
    public Dictionary<ApplicationStatus, int> StatusCounts { get; set; } = new();
    public List<JobApplication> RecentApplications { get; set; } = new();

    // Analytics
    public double SuccessRate { get; set; }
    public List<KeyValuePair<string, int>> TopCompanies { get; set; } = new();

    // Alerts
    public List<JobApplication> UpcomingDeadlines { get; set; } = new();
    public List<JobApplication> FollowUpSuggestions { get; set; } = new();
}
