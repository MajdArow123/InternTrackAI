namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Backs the anonymous-accessible public profile page (<c>/p/{slug}</c>). Deliberately a
/// narrow subset of <see cref="ProfileViewModel"/> — no email, no resume/cover letter
/// downloads, no raw application list — since this is rendered to unauthenticated visitors.
/// </summary>
public class PublicProfileViewModel
{
    public string DisplayName { get; set; } = "";
    public string? PhotoPath { get; set; }
    public List<string> Skills { get; set; } = new();
    public List<string> TargetRoles { get; set; } = new();
    public int TotalApplications { get; set; }
    public double SuccessRate { get; set; }

    public string? GitHubUsername { get; set; }
    public List<GitHubRepoDto>? GitHubRepos { get; set; }
}
