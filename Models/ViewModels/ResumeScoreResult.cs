namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Result of the AI scoring a user's resume in isolation (not against a specific job).
/// Used on the Profile page; transient API response shape, not persisted to the database.
/// </summary>
public class ResumeScoreResult
{
    public bool Success { get; set; }

    // Populated only when Success is false.
    public string? Error { get; set; }

    // 0-100 overall resume quality score.
    public int Score { get; set; }
    public string? Summary { get; set; }
    public List<string> Strengths { get; set; } = new();
    public List<string> Improvements { get; set; } = new();
}
