using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models.ViewModels;

public class ProfileViewModel
{
    public UserProfile Profile { get; set; } = new();
    public string? Email { get; set; }

    public List<ResumeVersion> Resumes { get; set; } = new();
    public List<CoverLetterVersion> CoverLetters { get; set; } = new();

    public List<string> Skills { get; set; } = new();
    public List<string> TargetRoles { get; set; } = new();

    public int TotalApplications { get; set; }
    public Dictionary<ApplicationStatus, int> StatusCounts { get; set; } = new();
    public double SuccessRate { get; set; }

    // Quick stats
    public int ApplicationsThisMonth { get; set; }
    public List<JobApplication> UpcomingDeadlines7Days { get; set; } = new();
    public List<JobApplication> RecentMatchedApps { get; set; } = new();

    public ResumeScoreResult? ScoreResult { get; set; }
}
