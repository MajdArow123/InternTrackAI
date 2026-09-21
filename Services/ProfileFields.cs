using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services;

/// <summary>
/// The one place the field-awareness profile fields are parsed, bounded and given display text.
/// Everything here has to survive junk: the values arrive either from a form post (a stale client,
/// or a hand-crafted one) or from a model's JSON in Phase 2's resume parser, so an unrecognised
/// value is always <c>null</c> or <see cref="FieldCategory.Other"/> — never an exception and never
/// a silently wrong member.
/// </summary>
public static class ProfileFields
{
    /// <summary>Longest <see cref="Models.UserProfile.Field"/> / <see cref="Models.UserProfile.Location"/> kept; anything longer is a paste, not a field.</summary>
    public const int MaxTextLength = 80;

    public const int MinYearsExperience = 0;
    public const int MaxYearsExperience = 60;

    /// <summary>Trims, collapses nothing else, and returns null for blank — the shape every nullable text column here wants.</summary>
    public static string? Text(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxTextLength ? trimmed[..MaxTextLength] : trimmed;
    }

    /// <summary>
    /// Parses a <see cref="FieldCategory"/> by name, case-insensitively. Blank → null (not set);
    /// an unrecognised name → <see cref="FieldCategory.Other"/>, because something was clearly
    /// meant and dropping it to null would silently discard the user's answer.
    /// </summary>
    public static FieldCategory? ParseCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TryParseName(value, out FieldCategory parsed) ? parsed : FieldCategory.Other;
    }

    /// <summary>
    /// Parses a <see cref="SeniorityLevel"/> by name, case-insensitively. Blank or unrecognised → null.
    /// Unlike the category there is no "Other" bucket to fall into, and guessing a level would change
    /// how every prompt pitches its answer, so an unknown value stays unset.
    /// </summary>
    public static SeniorityLevel? ParseSeniority(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TryParseName(value, out SeniorityLevel parsed) ? parsed : null;
    }

    /// <summary>
    /// <c>Enum.TryParse</c> by <b>name only</b>. It otherwise also accepts a numeric string — "5" would
    /// come back as <see cref="FieldCategory.Healthcare"/> — and a number arriving where a name belongs
    /// is a broken client, not a choice the user made. Everything here posts and parses names.
    /// </summary>
    private static bool TryParseName<TEnum>(string value, out TEnum parsed) where TEnum : struct, Enum
    {
        parsed = default;
        var name = value.Trim();
        if (name.Length == 0 || char.IsAsciiDigit(name[0]) || name[0] is '-' or '+') return false;

        return Enum.TryParse(name, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }

    /// <summary>Clamps to <see cref="MinYearsExperience"/>..<see cref="MaxYearsExperience"/>; null stays null.</summary>
    public static int? Years(int? value) =>
        value is null ? null : Math.Clamp(value.Value, MinYearsExperience, MaxYearsExperience);

    /// <summary>Human label for a category: "PublicSector" reads as "Public sector".</summary>
    public static string Display(FieldCategory category) => category switch
    {
        FieldCategory.PublicSector => "Public sector",
        _ => category.ToString()
    };

    /// <summary>Human label for a level: "EntryLevel" reads as "Entry level".</summary>
    public static string Display(SeniorityLevel seniority) => seniority switch
    {
        SeniorityLevel.EntryLevel => "Entry level",
        _ => seniority.ToString()
    };

    /// <summary>
    /// How the field reads in a prompt: "Registered Nursing (Healthcare)", or just the free text when
    /// no category is set. Null when there is no field at all. One definition so
    /// <see cref="UserContextBuilder"/> and <see cref="FollowUpService"/> can't word it differently.
    /// </summary>
    public static string? Label(string? field, FieldCategory? category)
    {
        var text = Text(field);
        if (text is null) return null;
        return category is { } c ? $"{text} ({Display(c)})" : text;
    }
}
