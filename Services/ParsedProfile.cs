using System.Text.Json;
using System.Text.Json.Serialization;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services;

/// <summary>How sure the model was that a skill is really on the resume.</summary>
/// <remarks>
/// Only <see cref="Low"/> changes behaviour: it renders <b>unchecked</b> on the review screen, so a
/// shaky skill takes a deliberate click to reach the profile rather than a deliberate click to keep
/// out. Anything the model spells differently parses as <see cref="Medium"/>.
/// </remarks>
public enum SkillConfidence
{
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>
/// One skill the model claims the resume demonstrates, with the snippet it is claiming it from.
/// </summary>
/// <remarks>
/// <see cref="Evidence"/> is the anti-hallucination measure that actually works here: a model
/// required to quote the source invents far fewer skills than one merely told not to, and the quote
/// gives the review screen something honest to show under each row. A skill that arrives without
/// evidence is dropped in <see cref="ParsedProfile.FromJson"/> — the rule is enforced, not just asked for.
/// </remarks>
public sealed record ParsedSkill(string Name, string Evidence, SkillConfidence Confidence);

/// <summary>One education entry. Shown read-only on the review screen; the profile has no column for it.</summary>
public sealed record ParsedEducation(string Institution, string? Credential, string? EndDate);

/// <summary>
/// Everything one resume parse produced. This is a <b>proposal</b>: nothing here reaches the profile
/// until the user confirms it on <c>/Profile/ReviewResume</c>.
/// </summary>
public sealed record ParsedProfile
{
    public string? FullName { get; init; }
    public string? Field { get; init; }
    public FieldCategory? Category { get; init; }
    public SeniorityLevel? Seniority { get; init; }
    public int? YearsExperience { get; init; }
    public string? Location { get; init; }
    public List<ParsedSkill> Skills { get; init; } = new();
    public List<string> TargetRoles { get; init; } = new();
    public List<ParsedEducation> Education { get; init; } = new();
    public string? Summary { get; init; }

    /// <summary>Caps, applied on read. The model is asked for these in the prompt; nothing trusts it to comply.</summary>
    public const int MaxSkills = 25;
    public const int MaxTargetRoles = 8;
    public const int MaxEducation = 6;
    public const int MaxEvidenceLength = 220;
    public const int MaxSummaryLength = 300;

    private static readonly JsonSerializerOptions Relaxed = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Reads the model's JSON into a bounded, validated proposal. Never throws and never trusts a
    /// value: an unrecognised category becomes <see cref="FieldCategory.Other"/>, an unrecognised
    /// level becomes null (both via <see cref="ProfileFields"/>), years are clamped, lists are capped
    /// and deduped, and a skill with no evidence is dropped entirely.
    /// </summary>
    public static ParsedProfile FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        return new ParsedProfile
        {
            FullName        = ProfileFields.Text(Str(r, "fullName")),
            Field           = ProfileFields.Text(Str(r, "field")),
            Category        = ProfileFields.ParseCategory(Str(r, "fieldCategory")),
            Seniority       = ProfileFields.ParseSeniority(Str(r, "seniority")),
            YearsExperience = ProfileFields.Years(Int(r, "yearsExperience")),
            Location        = ProfileFields.Text(Str(r, "location")),
            Skills          = ReadSkills(r),
            TargetRoles     = ProfileTags.Dedupe(StrArray(r, "targetRoles")).Take(MaxTargetRoles).ToList(),
            Education       = ReadEducation(r),
            Summary         = Truncate(PromptData.OneLine(Str(r, "summary")), MaxSummaryLength)
        };
    }

    /// <summary>Round-trips a stored draft's <c>RawJson</c>; a draft that no longer parses reads as empty rather than throwing.</summary>
    public static ParsedProfile FromJsonOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ParsedProfile();
        try { return FromJson(json); }
        catch (JsonException) { return new ParsedProfile(); }
    }

    private static List<ParsedSkill> ReadSkills(JsonElement root)
    {
        if (!root.TryGetProperty("skills", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skills = new List<ParsedSkill>();

        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;

            var name = ProfileTags.Normalize(Str(e, "name"));
            // No evidence, no skill. The prompt asks the model to omit these; this is what makes it true.
            var evidence = PromptData.OneLine(Str(e, "evidence"));
            if (name.Length == 0 || evidence.Length == 0) continue;
            if (!seen.Add(name)) continue;

            skills.Add(new ParsedSkill(name, Truncate(evidence, MaxEvidenceLength)!, ParseConfidence(Str(e, "confidence"))));
            if (skills.Count == MaxSkills) break;
        }

        return skills;
    }

    private static List<ParsedEducation> ReadEducation(JsonElement root)
    {
        if (!root.TryGetProperty("education", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => new ParsedEducation(
                PromptData.OneLine(Str(e, "institution")),
                ProfileFields.Text(PromptData.OneLine(Str(e, "credential"))),
                ProfileFields.Text(PromptData.OneLine(Str(e, "endDate")))))
            .Where(e => e.Institution.Length > 0)
            .Take(MaxEducation)
            .ToList();
    }

    /// <summary>
    /// Only "low" and "high" are recognised; anything else — including a missing or misspelled value —
    /// is <see cref="SkillConfidence.Medium"/>. Medium is the safe default: it renders checked like
    /// the model's normal output, where reading a typo as "low" would silently hide real skills.
    /// </summary>
    public static SkillConfidence ParseConfidence(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low"  => SkillConfidence.Low,
        "high" => SkillConfidence.High,
        _      => SkillConfidence.Medium
    };

    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static List<string> StrArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return new();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + " …";
    }
}

/// <summary>
/// What one parse produced: <see cref="Success"/> false carries a user-facing <see cref="Error"/>,
/// and <see cref="RawJson"/> is the model's reply verbatim for <see cref="Models.ParsedResume"/>.
/// </summary>
public sealed record ProfileExtraction(bool Success, ParsedProfile Profile, string RawJson, string? Error)
{
    public static ProfileExtraction Failed(string error) => new(false, new ParsedProfile(), "", error);
    public static ProfileExtraction Ok(ParsedProfile profile, string rawJson) => new(true, profile, rawJson, null);
}

/// <summary>
/// Reads a resume into a <see cref="ParsedProfile"/> proposal. The OpenAI-backed implementation is
/// <see cref="ProfileExtractorService"/>; tests substitute a scripted one.
/// </summary>
public interface IProfileExtractor
{
    Task<ProfileExtraction> ExtractAsync(string resumeText, CancellationToken ct = default);
}
