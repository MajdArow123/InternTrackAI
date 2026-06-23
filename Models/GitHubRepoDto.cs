namespace InternTrackAI.Models;

/// <summary>A single repo as returned by the public GitHub REST API, trimmed to the fields the profile UI needs.</summary>
public class GitHubRepoDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Url { get; set; } = string.Empty;
    public int Stars { get; set; }
    public string? Language { get; set; }
}
