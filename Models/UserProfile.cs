using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

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

    // ── Field awareness ──
    // Every one of these is nullable and every reader must cope with null: the app shipped without
    // them, so existing profiles have none set and must keep working unchanged. They exist for one
    // reason — Services/UserContextBuilder.cs turns them into the context block that goes into every
    // AI prompt, so a nursing or accounting user stops getting software-engineering answers.

    // Free text, the user's own words for what they do: "Software Engineering", "Registered Nursing".
    // This is the line that actually makes AI output domain-specific; FieldCategory only coarsens it
    // for things that need a fixed set, like the target-role suggestion map.
    public string? Field { get; set; }

    public FieldCategory? FieldCategory { get; set; }
    public SeniorityLevel? Seniority { get; set; }

    // Clamped to ProfileFields.MinYearsExperience..MaxYearsExperience on save.
    public int? YearsExperience { get; set; }

    // City/region, e.g. "Toronto, ON". Deliberately finer-grained than Country, which predates this
    // and is storage-only (it has never reached a prompt); Location is the one that does.
    public string? Location { get; set; }

    // When a resume parse was last confirmed on the review screen (Services/ProfileAutoFillService.cs).
    // Null means never — including for everyone who filled this profile in by hand.
    public DateTime? ProfileLastEnrichedAt { get; set; }

    // GitHub username shown as a "Projects" section on the profile page, with a handful of
    // repos pulled live from the public GitHub API.
    public string? GitHubUsername { get; set; }

    // Days after applying (or after the last contact) before an application in Applied shows up
    // as "follow-up due". Editable on the profile page within ReminderService.Min/MaxFollowUpAfterDays.
    public int FollowUpAfterDays { get; set; } = 7;

    // Secret for the anonymous iCalendar feed (GET /Calendar/feed.ics?token=…): 32 random bytes,
    // Base64Url-encoded. Null until the user first opens the profile page; "Regenerate link"
    // replaces it, which invalidates any calendar subscription using the old URL.
    public string? CalendarToken { get; set; }

    // IANA time zone every timestamp is shown in and every date/time the user types is read in
    // (see Services/UserClock.cs). Storage stays UTC. Defaults to Toronto for existing and new rows.
    public string TimeZoneId { get; set; } = TimeZones.DefaultZoneId;
}
