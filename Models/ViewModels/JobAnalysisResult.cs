namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Result of the AI Job Analyzer parsing a pasted job description or URL. Returned as
/// JSON from <c>AnalyzerController.Analyze</c> and used client-side to auto-fill the
/// Create Application form. Not persisted — this is a transient API response shape only.
/// </summary>
public class JobAnalysisResult
{
    public bool Success { get; set; }

    // Populated only when Success is false; shown to the user as-is.
    public string? Error { get; set; }
    public string? CompanyName { get; set; }
    public string? RoleTitle { get; set; }
    public string? Location { get; set; }
    public string? Salary { get; set; }

    // Up to 8 required skills extracted by the AI from the job text.
    public List<string> Skills { get; set; } = new();
}
