using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// One skill row on the review screen: what the model proposed, the snippet it proposed it from, and
/// whether the box starts ticked.
/// </summary>
public sealed record ReviewSkillRow(string Name, string Evidence, SkillConfidence Confidence)
{
    /// <summary>
    /// Low-confidence skills start <b>unchecked</b>. Keeping a shaky skill should take a deliberate
    /// click; letting one through should not.
    /// </summary>
    public bool Checked => Confidence != SkillConfidence.Low;

    public bool IsLowConfidence => Confidence == SkillConfidence.Low;
}

/// <summary>
/// Backs <c>/Profile/ReviewResume</c> — the screen standing between an AI parse and the profile.
/// Everything here is a <b>proposal</b>; the profile is untouched until the user posts the form.
/// </summary>
public class ResumeReviewViewModel
{
    public int DraftId { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>What the parse produced, already validated and bounded by <see cref="ParsedProfile.FromJson"/>.</summary>
    public ParsedProfile Parsed { get; set; } = new();

    public List<ReviewSkillRow> Skills { get; set; } = new();
    public List<string> TargetRoles { get; set; } = new();

    /// <summary>
    /// The profile as it stands, so each row can say what it would replace. The user sees both the
    /// parsed value and the stored one and decides — hiding the parse would make a career change
    /// invisible, and hiding the stored value is the silent overwrite this screen exists to prevent.
    /// </summary>
    public UserProfile Current { get; set; } = new();

    /// <summary>Skills and roles already on the profile; their rows say "already on your profile" instead of being offered as new.</summary>
    public HashSet<string> ExistingSkills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExistingRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which resume version this came from, for the "parsed from v2 — Backend focus" line. Null if it has since been deleted.</summary>
    public ResumeVersion? Source { get; set; }

    /// <summary>The demo account can review and apply, but its profile is restored nightly; the screen says so.</summary>
    public bool IsDemoAccount { get; set; }

    public bool IsNewSkill(string name) => !ExistingSkills.Contains(ProfileTags.Normalize(name));
    public bool IsNewRole(string name)  => !ExistingRoles.Contains(ProfileTags.Normalize(name));

    /// <summary>A scalar the parse would change, or null when it matches what is stored (or the parse found nothing).</summary>
    public string? Replaces(string? parsedValue, string? currentValue) =>
        !string.IsNullOrWhiteSpace(parsedValue)
        && !string.IsNullOrWhiteSpace(currentValue)
        && !string.Equals(parsedValue.Trim(), currentValue.Trim(), StringComparison.OrdinalIgnoreCase)
            ? currentValue
            : null;
}

/// <summary>One posted skill row. <see cref="Selected"/> false means the row is never read.</summary>
public class ReviewSkillInput
{
    public bool Selected { get; set; }
    public string? Name { get; set; }
}

/// <summary>One posted target-role row.</summary>
public class ReviewRoleInput
{
    public bool Selected { get; set; }
    public string? Name { get; set; }
}

/// <summary>
/// What the review form posts back. Bound by index (<c>Skills[0].Selected</c>), so an unchecked box
/// simply carries <c>false</c> and its name is dropped server-side rather than being filtered client-side.
/// </summary>
public class ResumeReviewInput
{
    public int DraftId { get; set; }

    public string? FullName { get; set; }
    public string? Field { get; set; }
    public string? FieldCategory { get; set; }
    public string? Seniority { get; set; }
    public int? YearsExperience { get; set; }
    public string? Location { get; set; }

    public List<ReviewSkillInput> Skills { get; set; } = new();
    public List<ReviewRoleInput> Roles { get; set; } = new();

    /// <summary>
    /// Turns the post into the confirmed selection the merge takes. Unchecked and blank rows are
    /// dropped here, which is the only place that decision is made.
    /// </summary>
    public ReviewedProfile ToReviewed() => new()
    {
        FullName        = FullName,
        Field           = Field,
        Category        = ProfileFields.ParseCategory(FieldCategory),
        Seniority       = ProfileFields.ParseSeniority(Seniority),
        YearsExperience = YearsExperience,
        Location        = Location,
        Skills          = ProfileTags.Dedupe(Skills.Where(s => s.Selected).Select(s => s.Name)),
        TargetRoles     = ProfileTags.Dedupe(Roles.Where(r => r.Selected).Select(r => r.Name))
    };
}
