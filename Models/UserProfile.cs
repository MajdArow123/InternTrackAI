namespace InternTrackAI.Models;

/// <summary>
/// Career portfolio data for a user (one row per user). Separate from ASP.NET Identity's
/// own user table since it holds app-specific profile fields, not auth/account data.
/// </summary>
public class UserProfile
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public int? Age { get; set; }
    public string? Country { get; set; }
    public string? PhoneNumber { get; set; }
    public string? PhotoFileName { get; set; }

    // Bumped whenever a new photo is uploaded; appended as a cache-busting query string
    // so the browser doesn't keep showing a stale cached image at the same file name.
    public int PhotoVersion { get; set; }
    public string? DisplayName { get; set; }

    // User's skill tags, stored as a JSON-serialized string array rather than a
    // normalized join table. Skills here are only ever read/written as a complete list
    // (used as AI context, displayed as tags) and never queried individually, so a single
    // JSON column is simpler than a separate Skills table + FK relationship.
    public string? SkillsJson { get; set; }

    // Target job titles the user is pursuing, same JSON-column rationale as SkillsJson.
    public string? TargetRolesJson { get; set; }
}
