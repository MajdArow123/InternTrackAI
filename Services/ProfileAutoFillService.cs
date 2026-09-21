using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>
/// Exactly what the user confirmed on the review screen. Only the rows they left checked are here —
/// an unchecked skill never reaches this type, let alone the profile.
/// </summary>
public sealed record ReviewedProfile
{
    public string? FullName { get; init; }
    public string? Field { get; init; }
    public FieldCategory? Category { get; init; }
    public SeniorityLevel? Seniority { get; init; }
    public int? YearsExperience { get; init; }
    public string? Location { get; init; }
    public List<string> Skills { get; init; } = new();
    public List<string> TargetRoles { get; init; } = new();
}

/// <summary>
/// The profile's values after a merge, plus what the merge actually added, so the caller can render
/// the result without a second round trip.
/// </summary>
public sealed record ProfileAutoFillResult(
    bool Success,
    string? Error,
    bool NameFilled,
    List<string> AddedSkills,
    List<string> AddedRoles,
    string? FullName,
    List<string> Skills,
    List<string> TargetRoles)
{
    public static ProfileAutoFillResult Failed(string error) => new(false, error, false, new(), new(), null, new(), new());

    public int SkillsAdded => AddedSkills.Count;
    public int RolesAdded  => AddedRoles.Count;
    public bool AddedAnything => NameFilled || SkillsAdded > 0 || RolesAdded > 0;

    /// <summary>Toast-ready fragment, e.g. "added 6 skills and 2 target roles" or "nothing new to add".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (SkillsAdded > 0) parts.Add($"{SkillsAdded} skill{(SkillsAdded == 1 ? "" : "s")}");
            if (RolesAdded  > 0) parts.Add($"{RolesAdded} target role{(RolesAdded == 1 ? "" : "s")}");
            var added = parts.Count switch
            {
                0 => "",
                1 => parts[0],
                _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1]
            };
            if (NameFilled && parts.Count == 0) return "filled in your name";
            if (NameFilled) return $"filled in your name and added {added}";
            if (parts.Count == 0) return "nothing new to add";
            return $"added {added}";
        }
    }
}

/// <summary>
/// The one place AI-derived data is written to a profile — and it is only ever reached from
/// <c>ProfileController.ApplyResumeReview</c>, after the user has confirmed a
/// <see cref="ParsedResume"/> draft on the review screen.
/// </summary>
/// <remarks>
/// <para>
/// This service used to take raw extractor output and merge it the moment an upload finished. It now
/// takes a <see cref="ReviewedProfile"/> — what the user ticked — and there is no path from the
/// extractor to here that skips the screen. That is the point: an invented skill has to be looked at
/// and left checked before it can land.
/// </para>
/// <para>
/// The merge rules are unchanged and non-negotiable: <b>tags are added, never removed.</b>
/// <see cref="ProfileTags.Merge"/> is the single definition of "already have this one" (trim,
/// collapse whitespace, case-insensitive, first casing wins), so re-applying the same resume twice
/// adds nothing the second time and a hand-curated tag survives every parse.
/// </para>
/// </remarks>
public class ProfileAutoFillService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ProfileAutoFillService> _logger;

    public ProfileAutoFillService(ApplicationDbContext db, ILogger<ProfileAutoFillService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Merges a confirmed review into the profile and marks the draft applied, in one transaction's
    /// worth of work: a draft must not be able to read as applied when the merge didn't happen, or
    /// the user loses the review with nothing to show for it.
    /// </summary>
    /// <remarks>
    /// Scalars (field, seniority, location, years, name) take the value the user confirmed — they
    /// looked at the box and could have changed it, so an edit wins by construction and there is no
    /// hidden "only if null" rule to explain. Tags are the additive union described on the type.
    /// </remarks>
    public async Task<ProfileAutoFillResult> ApplyAsync(string userId, ParsedResume draft, ReviewedProfile reviewed, CancellationToken ct = default)
    {
        if (draft.UserId != userId)
            return ProfileAutoFillResult.Failed("That resume analysis isn't yours.");
        if (draft.Applied)
            return ProfileAutoFillResult.Failed("That resume analysis has already been applied.");

        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null)
        {
            profile = new UserProfile { UserId = userId };
            _db.UserProfiles.Add(profile);
        }

        var nameFilled = false;
        var name = ProfileFields.Text(reviewed.FullName);
        if (name is not null && !string.Equals(name, profile.FullName, StringComparison.Ordinal))
        {
            profile.FullName = name;
            nameFilled = true;
        }

        if (ProfileFields.Text(reviewed.Field) is { } field) profile.Field = field;
        if (reviewed.Category is { } category)               profile.FieldCategory = category;
        if (reviewed.Seniority is { } seniority)             profile.Seniority = seniority;
        if (ProfileFields.Years(reviewed.YearsExperience) is { } years) profile.YearsExperience = years;
        if (ProfileFields.Text(reviewed.Location) is { } location)      profile.Location = location;

        var (skills, addedSkills) = ProfileTags.Merge(ProfileTags.FromJson(profile.SkillsJson), reviewed.Skills);
        var (roles,  addedRoles)  = ProfileTags.Merge(ProfileTags.FromJson(profile.TargetRolesJson), reviewed.TargetRoles);
        profile.SkillsJson      = ProfileTags.ToJson(skills);
        profile.TargetRolesJson = ProfileTags.ToJson(roles);
        profile.ProfileLastEnrichedAt = DateTime.UtcNow;

        var row = await _db.ParsedResumes.FirstOrDefaultAsync(p => p.Id == draft.Id && p.UserId == userId, ct);
        if (row is null) return ProfileAutoFillResult.Failed("That resume analysis is no longer available.");
        row.Applied = true;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Applied resume review {DraftId} for user {UserId}: name={NameFilled}, +{Skills} skills, +{Roles} roles.",
            draft.Id, userId, nameFilled, addedSkills.Count, addedRoles.Count);

        return new ProfileAutoFillResult(true, null, nameFilled, addedSkills, addedRoles, profile.FullName, skills, roles);
    }
}
