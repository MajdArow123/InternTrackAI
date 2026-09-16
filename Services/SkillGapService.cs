using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>
/// "Skills you're missing most": aggregates the <see cref="JobApplication.MissingSkillsJson"/> the resume
/// matcher already stored. No AI calls — pure counting, rules in <see cref="Build"/> (unit-tested) plus a DI
/// wrapper that loads the user's rows.
///
/// Rules:
/// <list type="bullet">
/// <item>An application counts as analyzed when it was actually sent (any status but Saved — a rejection is the
/// most informative signal) and its MissingSkillsJson parses as a JSON array. "[]" is analyzed with nothing
/// missing (it still counts toward "of 31"); null, blank, and malformed JSON are not analyzed.</item>
/// <item>Skills are normalised with <see cref="ProfileTags.Normalize"/> (trim, collapse whitespace), compared
/// case-insensitively, and folded through the small <see cref="Aliases"/> map. No fuzzy matching.</item>
/// <item>Counts are distinct applications: a skill listed twice in one application counts once.</item>
/// <item>Display name: the alias map's canonical spelling for aliased skills, otherwise the most common casing
/// seen (ties go to the spelling seen first).</item>
/// <item>Role buckets: an application belongs to a target-role tag when a strict majority of the tag's significant
/// words (filler like "intern", "co-op", "senior", "II" ignored) appear in its role title, after both sides are
/// suffix-normalised ("engineer" = "engineering", "developer" = "development"). Deterministic: no AI, no fuzzy
/// distance. An application can belong to several tags; one matching none goes to "Other".</item>
/// </list>
/// </summary>
public class SkillGapService
{
    /// <summary>Fewer analyzed applications than this and the card is hidden.</summary>
    public const int MinimumAnalyzed = 3;

    /// <summary>From this many analyzed applications on, the "gets clearer" note goes away.</summary>
    public const int ConfidentAnalyzed = 5;

    public const int DefaultTopN = 10;

    public const string AllKey   = "all";
    public const string OtherKey = "other";
    public const string RoleKeyPrefix = "role:";

    public const string NoPattern     = "No clear pattern yet — no single skill is missing more than once.";
    public const string NoRoleData    = "No missing-skill data for this role yet.";
    public const string NothingMissing = "Nothing missing so far — your resume covered every requirement in the postings you've analyzed.";

    /// <summary>
    /// Obvious spellings of the same skill, shared with the keyword-coverage check — see
    /// <see cref="SkillAliases"/>, which is where the map itself lives and where new spellings are added.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Aliases => SkillAliases.Map;

    private static readonly Regex Words = new(@"[\p{L}\p{N}+#]+", RegexOptions.Compiled);

    // ── Role matching tables (see MatchesRole / Stem) ─────

    /// <summary>
    /// Words that say nothing about what kind of role it is: seniority, internship/co-op markers, levels, seasons,
    /// and connectives. Compared before stemming, so list each spelling.
    /// </summary>
    public static readonly IReadOnlySet<string> RoleFillerWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "intern", "interns", "internship", "internships", "coop", "coops", "trainee", "apprentice",
        "junior", "jr", "senior", "sr", "i", "ii", "iii", "iv", "entrylevel", "new", "grad", "graduate", "student",
        "summer", "fall", "autumn", "winter", "spring",
        "and", "of", "the", "for", "to", "a", "an", "in", "at", "with",
    };

    /// <summary>Two-word role terms written either joined, hyphenated or spaced; all three become the joined form.</summary>
    private static readonly (Regex Pattern, string Joined)[] RoleCompounds =
        new[] { ("front", "end"), ("back", "end"), ("full", "stack"), ("dev", "ops"), ("co", "op"), ("entry", "level") }
            .Select(p => (new Regex($@"\b{p.Item1}[\s\-‐-–]*{p.Item2}\b", RegexOptions.Compiled), p.Item1 + p.Item2))
            .ToArray();

    /// <summary>
    /// Ordered suffix rules for <see cref="Stem"/>; the first match wins, so longer suffixes come before the shorter
    /// ones they end with ("eers" before "ers" before "s"). "eer" maps to itself so "engineer" is not cut to "engine".
    /// </summary>
    private static readonly (string Suffix, string Replacement)[] SuffixRules =
    {
        ("ytical", "y"), ("ytics", "y"), ("yzers", "y"), ("yzer", "y"), ("ytic", "y"), ("ysis", "y"), ("ysts", "y"), ("yze", "y"), ("yst", "y"),
        ("ntific", "n"), ("ntists", "n"), ("ntist", "n"), ("nces", "n"), ("nce", "n"),
        ("eering", "eer"), ("eers", "eer"), ("eer", "eer"),
        ("ations", "at"), ("ation", "at"),
        ("ments", ""), ("ment", ""),
        ("ings", ""), ("ing", ""),
        ("ers", ""), ("er", ""),
        ("ors", ""), ("or", ""),
        ("ies", "y"),
        ("s", ""),
    };

    private readonly ApplicationDbContext _db;

    public SkillGapService(ApplicationDbContext db) => _db = db;

    /// <summary>Loads the user's applications and target roles and builds the card's view model.</summary>
    public async Task<SkillGapViewModel> GetSkillGapsAsync(string userId, string? targetRoleFilter = null, int topN = DefaultTopN)
    {
        var apps = await _db.JobApplications.AsNoTracking()
            .Where(a => a.UserId == userId && a.Status != ApplicationStatus.Saved && a.MissingSkillsJson != null)
            .ToListAsync();
        var rolesJson = await _db.UserProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.TargetRolesJson)
            .FirstOrDefaultAsync();

        return Build(apps, ProfileTags.FromJson(rolesJson), targetRoleFilter, topN);
    }

    /// <summary>Pure computation over already-loaded rows (callers are responsible for scoping them to one user).</summary>
    public static SkillGapViewModel Build(IEnumerable<JobApplication> applications, IEnumerable<string>? targetRoles,
                                          string? targetRoleFilter = null, int topN = DefaultTopN)
    {
        var analyzed = applications
            .Where(Counts)
            .Select(a => (App: a, Skills: ParseSkills(a.MissingSkillsJson)))
            .Where(x => x.Skills is not null)
            .OrderByDescending(x => x.App.DateApplied ?? DateTime.MinValue)
            .ThenByDescending(x => x.App.Id)
            .Select(x => new Analyzed(x.App, x.Skills!))
            .ToList();

        // Display spelling per canonical key is decided once across everything, so a skill reads the same in every bucket.
        var names = DisplayNames(analyzed);

        var all   = Bucket(AllKey, "All", analyzed, names, topN, NothingMissing);
        var tags  = ProfileTags.Dedupe(targetRoles);
        var roles = tags
            .Select(tag => Bucket(RoleKeyPrefix + tag, tag, analyzed.Where(a => MatchesRole(a.App.RoleTitle, tag)).ToList(), names, topN, NoRoleData))
            .ToList();

        var unmatched = analyzed.Where(a => !tags.Any(t => MatchesRole(a.App.RoleTitle, t))).ToList();
        var other = tags.Count > 0 && unmatched.Count > 0
            ? Bucket(OtherKey, "Other", unmatched, names, topN, NoRoleData)
            : null;

        var selected = string.IsNullOrWhiteSpace(targetRoleFilter)
            ? all
            : roles.FirstOrDefault(r => ProfileTags.Same(r.Label, targetRoleFilter))
              ?? (string.Equals(targetRoleFilter, OtherKey, StringComparison.OrdinalIgnoreCase) && other is not null ? other : null)
              ?? new SkillGapBucket(RoleKeyPrefix + ProfileTags.Normalize(targetRoleFilter), ProfileTags.Normalize(targetRoleFilter), 0, new(), NoRoleData);

        var referenced = roles.Append(all).Append(selected).Concat(other is null ? Array.Empty<SkillGapBucket>() : new[] { other })
            .SelectMany(b => b.Skills).SelectMany(s => s.ApplicationIds).ToHashSet();

        return new SkillGapViewModel
        {
            AnalyzedApplications = analyzed.Count,
            All          = all,
            Selected     = selected,
            Roles        = roles,
            Other        = other,
            Applications = analyzed
                .Where(a => referenced.Contains(a.App.Id))
                .ToDictionary(a => a.App.Id, a => new SkillGapApplication(a.App.Id, a.App.CompanyName, a.App.RoleTitle))
        };
    }

    /// <summary>Sent applications only. Saved ones were never submitted, so they say nothing about outcomes.</summary>
    public static bool Counts(JobApplication a) => a.Status != ApplicationStatus.Saved;

    /// <summary>
    /// The stored array's non-blank string entries, or null when the column is null/blank, malformed, or not an
    /// array (non-string entries are skipped rather than failing the whole application).
    /// </summary>
    public static List<string>? ParseSkills(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => ProfileTags.Normalize(e.GetString()))
                .Where(s => s.Length > 0)
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The comparison key: normalised, lower-cased, then folded through <see cref="Aliases"/>.</summary>
    public static string Key(string skill)
    {
        var lower = ProfileTags.Normalize(skill).ToLowerInvariant();
        return Aliases.TryGetValue(lower, out var canonical) ? canonical.ToLowerInvariant() : lower;
    }

    /// <summary>
    /// True when a strict majority of the tag's significant words appear in the role title, comparing
    /// <see cref="Stem"/>med whole words. Significant = not in <see cref="RoleFillerWords"/>. So "Software Engineer"
    /// matches "Software Engineering Intern" (2 of 2), "Data Analyst" does not match "Business Analyst" (1 of 2),
    /// and a three-word tag needs two. A tag made only of filler words never matches.
    /// </summary>
    public static bool MatchesRole(string? roleTitle, string? tag)
    {
        var significant = RoleWords(tag).Where(w => !RoleFillerWords.Contains(w)).Select(Stem).Distinct().ToList();
        if (significant.Count == 0) return false;
        var title = RoleWords(roleTitle).Select(Stem).ToHashSet();
        return significant.Count(title.Contains) * 2 > significant.Count;
    }

    /// <summary>
    /// Deterministic suffix normalisation for role words: the first matching rule in <see cref="SuffixRules"/> is
    /// applied once (only if at least three letters remain), then a trailing "e" is dropped from words longer than
    /// four letters. It only has to map related forms to the same key, not to a real root:
    /// engineer/engineering → "engineer", develop/developer/development → "develop", analyst/analytics/analysis →
    /// "analy", science/scientist → "scien", manage/manager/management → "manag".
    /// </summary>
    public static string Stem(string word)
    {
        var w = word.ToLowerInvariant();
        foreach (var (suffix, replacement) in SuffixRules)
        {
            if (!w.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (suffix == "s" && (w.EndsWith("ss") || w.EndsWith("us") || w.EndsWith("is"))) break;
            var stem = w[..^suffix.Length] + replacement;
            if (stem.Length >= 3) w = stem;
            break;
        }
        return w.Length > 4 && w.EndsWith('e') ? w[..^1] : w;
    }

    /// <summary>
    /// "Docker came up in 12 of your 31 analyzed applications (39%)." — or, when the top three are within two
    /// counts of each other, "Docker, Kubernetes, and GraphQL come up most often." — or the no-pattern line when
    /// nothing is missing more than once.
    /// </summary>
    public static string Takeaway(IReadOnlyList<SkillGapEntry> skills, int analyzed, string emptyMessage)
    {
        if (skills.Count == 0) return emptyMessage;
        if (skills.All(s => s.Count <= 1)) return NoPattern;

        if (skills.Count >= 3 && skills[0].Count - skills[2].Count <= 2)
            return $"{skills[0].Skill}, {skills[1].Skill}, and {skills[2].Skill} come up most often.";

        var top = skills[0];
        return string.Format(CultureInfo.InvariantCulture,
            "{0} came up in {1} of your {2} analyzed application{3} ({4}%).",
            top.Skill, top.Count, analyzed, analyzed == 1 ? "" : "s", top.Percent);
    }

    // ── Internals ────────────────────────────────────────

    private sealed record Analyzed(JobApplication App, List<string> Skills);

    private static SkillGapBucket Bucket(string key, string label, List<Analyzed> apps, IReadOnlyDictionary<string, string> names,
                                         int topN, string emptyMessage)
    {
        var skills = apps
            .SelectMany(a => a.Skills.Select(Key).Distinct().Select(k => (Key: k, a.App.Id)))
            .GroupBy(x => x.Key)
            .Select(g =>
            {
                var ids = g.Select(x => x.Id).ToList();
                return new SkillGapEntry(names[g.Key], ids.Count, Percent(ids.Count, apps.Count), ids);
            })
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.Skill, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, topN))
            .ToList();

        return new SkillGapBucket(key, label, apps.Count, skills, Takeaway(skills, apps.Count, emptyMessage));
    }

    private static IReadOnlyDictionary<string, string> DisplayNames(IEnumerable<Analyzed> apps)
    {
        var spellings = new Dictionary<string, List<string>>();   // key → every spelling, in order seen
        foreach (var skill in apps.SelectMany(a => a.Skills))
        {
            var key = Key(skill);
            if (!spellings.TryGetValue(key, out var list)) spellings[key] = list = new List<string>();
            list.Add(skill);
        }

        return spellings.ToDictionary(kv => kv.Key, kv =>
            Aliases.TryGetValue(kv.Key, out var canonical)
                ? canonical
                : kv.Value
                    .Select((s, i) => (s, i))
                    .GroupBy(x => x.s, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Min(x => x.i))
                    .First().Key);
    }

    private static int Percent(int count, int total) =>
        total == 0 ? 0 : (int)Math.Round(count * 100.0 / total, MidpointRounding.AwayFromZero);

    /// <summary>Lower-cased words of a role title or tag, with <see cref="RoleCompounds"/> joined first and bare numbers dropped.</summary>
    private static List<string> RoleWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        var s = text.ToLowerInvariant();
        foreach (var (pattern, joined) in RoleCompounds) s = pattern.Replace(s, joined);
        return Words.Matches(s).Select(m => m.Value).Where(w => !w.All(char.IsDigit)).ToList();
    }
}
