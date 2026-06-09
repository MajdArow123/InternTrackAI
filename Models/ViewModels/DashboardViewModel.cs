using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models.ViewModels;

public class DashboardViewModel
{
    public int TotalApplications { get; set; }
    public Dictionary<ApplicationStatus, int> StatusCounts { get; set; } = new();
    public List<JobApplication> RecentApplications { get; set; } = new();
}
