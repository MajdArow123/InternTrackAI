using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>
/// Outcome of one resume → profile auto-fill. <see cref="Skills"/> / <see cref="TargetRoles"/> /
/// <see cref="FullName"/> are the profile's values <em>after</em> the merge, so a caller can re-render
/// the form without another round-trip.
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
/// Takes already-extracted resume text (see <see cref="ResumeTextService"/>), asks
/// <see cref="IProfileExtractor"/> for name / skills / target roles,
/// and merges them into the user's profile with the add-never-remove rules: a non-empty name is never
/// overwritten, and tags are added only when not already present (case-insensitive, see
/// <see cref="ProfileTags"/>). Shared by the automatic run after an upload and the manual
/// "Analyze with AI" button, so both behave identically. Rate limiting is the caller's job.
/// </summary>
public class ProfileAutoFillService
{
    private readonly ApplicationDbContext _db;
    private readonly IProfileExtractor _extractor;
    private readonly ILogger<ProfileAutoFillService> _logger;

    public ProfileAutoFillService(ApplicationDbContext db, IProfileExtractor extractor, ILogger<ProfileAutoFillService> logger)
    {
        _db = db;
        _extractor = extractor;
        _logger = logger;
    }

    /// <summary>
    /// Merges what the extractor finds in <paramref name="resumeText"/> into the user's profile. Callers get
    /// the text from <see cref="ResumeTextService"/>, which is also where "no resume / unreadable / no text"
    /// is decided — by the time we are here there is text worth sending.
    /// </summary>
    public async Task<ProfileAutoFillResult> FillFromTextAsync(string userId, string resumeText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resumeText))
            return ProfileAutoFillResult.Failed("No readable text found in the PDF.");

        var extracted = await _extractor.ExtractAsync(resumeText, ct);
        if (!extracted.Success)
            return ProfileAutoFillResult.Failed(extracted.Error ?? "Could not analyze the resume.");

        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null)
        {
            profile = new UserProfile { UserId = userId };
            _db.UserProfiles.Add(profile);
        }

        var nameFilled = false;
        if (!string.IsNullOrWhiteSpace(extracted.FullName) && string.IsNullOrWhiteSpace(profile.FullName))
        {
            profile.FullName = extracted.FullName.Trim();
            nameFilled = true;
        }

        var (skills, addedSkills) = ProfileTags.Merge(ProfileTags.FromJson(profile.SkillsJson), extracted.Skills);
        var (roles,  addedRoles)  = ProfileTags.Merge(ProfileTags.FromJson(profile.TargetRolesJson), extracted.TargetRoles);
        profile.SkillsJson      = ProfileTags.ToJson(skills);
        profile.TargetRolesJson = ProfileTags.ToJson(roles);

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Auto-filled profile for user {UserId}: name={NameFilled}, +{Skills} skills, +{Roles} roles.",
            userId, nameFilled, addedSkills.Count, addedRoles.Count);

        return new ProfileAutoFillResult(true, null, nameFilled, addedSkills, addedRoles, profile.FullName, skills, roles);
    }
}
