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

    // Sent count / response rate per resume version (ResumeAnalyticsService), shown inline in the resume list.
    public IReadOnlyDictionary<int, InternTrackAI.Services.ResumeStats> ResumeStats { get; set; } = new Dictionary<int, InternTrackAI.Services.ResumeStats>();
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

    // Null if no GitHub username is set, or if the API call failed/rate-limited.
    public List<GitHubRepoDto>? GitHubRepos { get; set; }

    // Absolute URL of the user's iCalendar feed (GET /Calendar/feed.ics?token=…); null only if
    // no token has been issued yet (ProfileController.Index issues one before rendering).
    public string? CalendarFeedUrl { get; set; }

    // ── Connected accounts (Gmail) ──
    // GmailConfigured is false when Google:ClientId/ClientSecret are absent: the card is not rendered at all.
    public bool GmailConfigured { get; set; }
    public GmailConnection? GmailConnection { get; set; }
    // The shared demo account can't link a real inbox: Connect renders disabled with a tooltip.
    public bool IsDemoAccount { get; set; }
}
