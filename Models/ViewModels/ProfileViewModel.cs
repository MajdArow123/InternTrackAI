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

    // Deserialized from UserProfile.SkillsJson/TargetRolesJson for direct use in the view.
    public List<string> Skills { get; set; } = new();
    public List<string> TargetRoles { get; set; } = new();

    // Target-role suggestions for every FieldCategory (Services/TargetRoleSeeds.cs), rendered as a
    // JSON island in _SkillsRoles.cshtml. The whole map ships rather than just the user's own
    // category so the combobox can follow the "Field category" dropdown without a page reload.
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RoleSuggestions { get; set; } =
        new Dictionary<string, IReadOnlyList<string>>();

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

    // "Rewrite a bullet": the user's applications that have a stored job description, newest first.
    public List<RewriteApplicationOption> RewriteApplications { get; set; } = new();

    // A resume parse waiting to be reviewed, if any. Set when the user uploaded or re-parsed and then
    // navigated away instead of confirming — without this the draft is stranded with no way back to it.
    public ParsedResume? PendingParse { get; set; }

    // True when the user has neither skills nor target roles: the two tag sections then point at the
    // resume upload instead of showing two empty boxes with no suggestion of what to do.
    public bool HasNoTags => Skills.Count == 0 && TargetRoles.Count == 0;
}

/// <summary>One entry in the bullet rewriter's application selector.</summary>
public sealed record RewriteApplicationOption(int Id, string CompanyName, string RoleTitle);
