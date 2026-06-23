using System.Text.Json;
using InternTrackAI.Models;

namespace InternTrackAI.Services;

/// <summary>
/// Fetches a user's public repos from the GitHub REST API for display on the profile and
/// public-profile pages. Uses the unauthenticated API (60 req/hr per IP) since this only
/// reads public data — no GitHub OAuth or token is needed.
/// </summary>
public class GitHubService
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(HttpClient http, ILogger<GitHubService> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("InternTrackAI");
    }

    /// <summary>
    /// Returns up to <paramref name="take"/> of the user's most recently updated public,
    /// non-fork repos, or null if the username is invalid, doesn't exist, or the API call
    /// fails for any reason (rate limit, network error) — callers should treat null as
    /// "nothing to show" rather than an error to surface to the user.
    /// </summary>
    public async Task<List<GitHubRepoDto>?> GetPublicReposAsync(string username, int take = 6)
    {
        try
        {
            var response = await _http.GetAsync(
                $"https://api.github.com/users/{Uri.EscapeDataString(username)}/repos?sort=updated&per_page=20");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub repos fetch returned {Status} for {User}", (int)response.StatusCode, username);
                return null;
            }

            var raw = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(raw);

            var repos = new List<GitHubRepoDto>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("fork", out var fork) && fork.GetBoolean())
                    continue;

                repos.Add(new GitHubRepoDto
                {
                    Name        = el.GetProperty("name").GetString() ?? "",
                    Description = el.TryGetProperty("description", out var d) ? d.GetString() : null,
                    Url         = el.GetProperty("html_url").GetString() ?? "",
                    Stars       = el.TryGetProperty("stargazers_count", out var s) ? s.GetInt32() : 0,
                    Language    = el.TryGetProperty("language", out var l) ? l.GetString() : null
                });
            }

            return repos
                .OrderByDescending(r => r.Stars)
                .Take(take)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch GitHub repos for {User}", username);
            return null;
        }
    }
}
