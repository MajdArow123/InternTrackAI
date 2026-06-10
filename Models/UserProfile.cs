namespace InternTrackAI.Models;

public class UserProfile
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public int? Age { get; set; }
    public string? Country { get; set; }
    public string? PhoneNumber { get; set; }
    public string? PhotoFileName { get; set; }
    public int PhotoVersion { get; set; }
    public string? SkillsJson { get; set; }
    public string? TargetRolesJson { get; set; }
}
