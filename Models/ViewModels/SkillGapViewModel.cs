namespace InternTrackAI.Models.ViewModels;

/// <summary>One bar of the "Skills you're missing most" chart.</summary>
public sealed record SkillGapEntry(
    string Skill,                 // display spelling: the alias map's canonical name, else the most common casing seen
    int Count,                    // distinct applications the skill was missing from
    int Percent,                  // Count as a share of the bucket's analyzed applications, rounded
    List<int> ApplicationIds);    // the applications behind Count, newest first (drill-down)

/// <summary>
/// The skill ranking for one slice of the analyzed applications: all of them, one target role, or
/// the "Other" bucket (matched no target role).
/// </summary>
public sealed record SkillGapBucket(
    string Key,                   // "all", "role:<tag>", or "other"
    string Label,                 // "All", the tag as the user typed it, or "Other"
    int Analyzed,                 // applications in this slice with match data (the "of 31" denominator)
    List<SkillGapEntry> Skills,   // top N, count descending
    string Takeaway);             // the computed one-liner shown above the chart

/// <summary>Company and role for one application listed in the drill-down.</summary>
public sealed record SkillGapApplication(int Id, string Company, string Role);

/// <summary>Everything the dashboard's skill gap card renders, built by <c>SkillGapService</c>.</summary>
public sealed class SkillGapViewModel
{
    /// <summary>Applications with match data (see SkillGapService for which statuses count).</summary>
    public int AnalyzedApplications { get; init; }

    /// <summary>Skills for the requested filter (All unless a target role was asked for).</summary>
    public SkillGapBucket Selected { get; init; } = EmptyBucket;

    public SkillGapBucket All { get; init; } = EmptyBucket;

    /// <summary>One bucket per target-role tag, in the profile's order (empty buckets included).</summary>
    public List<SkillGapBucket> Roles { get; init; } = new();

    /// <summary>Applications that matched no target role; null when there are none.</summary>
    public SkillGapBucket? Other { get; init; }

    /// <summary>Company/role for every application referenced by any bucket, keyed by id.</summary>
    public Dictionary<int, SkillGapApplication> Applications { get; init; } = new();

    /// <summary>Below the threshold the chart would be noise, so the card is not rendered at all.</summary>
    public bool Hide => AnalyzedApplications < Services.SkillGapService.MinimumAnalyzed;

    /// <summary>3–4 analyzed applications: the card shows with a "gets clearer" note.</summary>
    public bool LowSample => !Hide && AnalyzedApplications < Services.SkillGapService.ConfidentAnalyzed;

    /// <summary>Role pills only help with two or more target roles that each have data.</summary>
    public bool ShowRoleFilter => Roles.Count >= 2 && Roles.Count(r => r.Analyzed > 0) >= 2;

    private static readonly SkillGapBucket EmptyBucket = new("all", "All", 0, new(), "");
}
