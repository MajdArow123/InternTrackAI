namespace InternTrackAI.Models.ViewModels;

public class JobAnalysisResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? CompanyName { get; set; }
    public string? RoleTitle { get; set; }
    public string? Location { get; set; }
    public string? Salary { get; set; }
    public List<string> Skills { get; set; } = new();
}
