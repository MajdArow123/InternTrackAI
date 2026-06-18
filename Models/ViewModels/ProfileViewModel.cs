using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Backs the full Career Portfolio page (<c>/Profile</c>). Aggregates the user's profile
/// row, document version histories, deserialized skills/target roles, application stats,
/// and quick-stat widgets so the view doesn't need to query or parse anything itself.
/// </summary>
public class ProfileViewModel
{
    public UserProfile Profile { get; set; } = new();
    public string? Email { get; set; }

    public List<ResumeVersion> Resumes { get; set; } = new();
    public List<CoverLetterVersion> CoverLetters { get; set; } = new();

    // Deserialized from UserProfile.SkillsJson/TargetRolesJson for direct use in the view.
    public List<string> Skills { get; set; } = new();
    public List<string> TargetRoles { get; set; } = new();

    public int TotalApplications { get; set; }
    public Dictionary<ApplicationStatus, int> StatusCounts { get; set; } = new();
    public double SuccessRate { get; set; }

    // Quick stats
    public int ApplicationsThisMonth { get; set; }
    public List<JobApplication> UpcomingDeadlines7Days { get; set; } = new();

    // Most recent applications that have a MatchScore set, for the "recent resume match
    // scores" quick-stat widget.
    public List<JobApplication> RecentMatchedApps { get; set; } = new();

    // Null until the user has run the AI Resume Score feature at least once.
    public ResumeScoreResult? ScoreResult { get; set; }
}
