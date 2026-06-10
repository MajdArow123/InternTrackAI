namespace InternTrackAI.Models.ViewModels;

public class ResumeScoreResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int Score { get; set; }
    public string? Summary { get; set; }
    public List<string> Strengths { get; set; } = new();
    public List<string> Improvements { get; set; } = new();
}
