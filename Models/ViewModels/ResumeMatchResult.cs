namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Result of an AI resume-vs-job-description match. Returned as JSON from the
/// AutoMatch/match API endpoints; the relevant fields (Score, Recommendation, Summary,
/// MatchingSkills, MissingSkills) are also persisted onto the owning
/// <see cref="JobApplication"/> when the user saves the application, so the match can be
/// redisplayed later (e.g. in the detail drawer) without re-calling the AI.
/// </summary>
public class ResumeMatchResult
{
    public bool Success { get; set; }

    // Populated only when Success is false (e.g. no active resume, AI/network failure).
    public string? Error { get; set; }

    // 0-100 fit score.
    public int Score { get; set; }

    // Tier label derived from Score (e.g. "APPLY", "MAYBE", "SKIP").
    public string Recommendation { get; set; } = string.Empty;
    public List<string> MatchingSkills { get; set; } = new();
    public List<string> MissingSkills { get; set; } = new();

    // Currently computed by the AI but not persisted/displayed anywhere in the UI.
    public List<string> Strengths { get; set; } = new();
    public string? Summary { get; set; }
}
