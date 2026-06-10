namespace InternTrackAI.Models.ViewModels;

public class ResumeMatchResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int Score { get; set; }
    public string Recommendation { get; set; } = string.Empty;
    public List<string> MatchingSkills { get; set; } = new();
    public List<string> MissingSkills { get; set; } = new();
    public List<string> Strengths { get; set; } = new();
    public string? Summary { get; set; }
}
