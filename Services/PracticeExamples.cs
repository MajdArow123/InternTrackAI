using System.Text.Json;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services;

/// <summary>One static example question, shown muted on an empty practice page.</summary>
public sealed record PracticeExample(string Question, PracticeDifficulty Difficulty, QuestionCategory Category, string? Topic);

/// <summary>
/// The example card on an empty <c>/Practice</c>, keyed by <see cref="FieldCategory"/>. A first-time
/// visitor sees one question of the kind they would get <em>before</em> spending a generation on it —
/// and a nurse sees a nursing question, which says the app is not IT-only in the same way the demo's
/// nursing resume parse does.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="TargetRoleSeeds"/>: <c>Data/Seeds/practice-examples.json</c>, read once by a
/// singleton, unknown keys skipped, and a missing or malformed file logs a warning and leaves the page
/// with no example rather than an error. The example is a convenience, and the empty state already
/// says what to do without it.
/// </para>
/// <para>
/// <b>Never a <see cref="Models.PracticeQuestion"/>.</b> No row, no id, no AI call, not counted in the
/// progress card and not answerable. It is rendered by its own partial for the same reason: reusing the
/// real card with a flag would give it a star, an answer box and a <c>data-question-id</c>, and each of
/// those invites someone to treat it as real.
/// </para>
/// </remarks>
public class PracticeExamples
{
    public const string RelativePath = "Data/Seeds/practice-examples.json";

    /// <summary>The fallback key: a behavioural question that fits any field, for a profile with no field set.</summary>
    public const string DefaultKey = "Default";

    private readonly IReadOnlyDictionary<FieldCategory, PracticeExample> _byCategory;
    private readonly PracticeExample? _default;

    public PracticeExamples(IWebHostEnvironment env, ILogger<PracticeExamples> logger)
        : this(Path.Combine(env.ContentRootPath, RelativePath), logger) { }

    public PracticeExamples(string path, ILogger<PracticeExamples> logger)
    {
        try
        {
            (_byCategory, _default) = Parse(File.ReadAllText(path));
            logger.LogInformation("Practice examples loaded for {Count} field categories.", _byCategory.Count);
        }
        catch (Exception ex)
        {
            // Never fatal: an empty practice page is still usable, it just shows no example.
            logger.LogWarning(ex, "Could not load practice examples from {Path}; the empty practice page will show none.", path);
            _byCategory = new Dictionary<FieldCategory, PracticeExample>();
            _default = null;
        }
    }

    /// <summary>
    /// Parses the seed JSON. A key that is neither <see cref="DefaultKey"/> nor a
    /// <see cref="FieldCategory"/> member is skipped — the file's own <c>_comment</c> included — and so
    /// is an entry with no question text.
    /// </summary>
    public static (IReadOnlyDictionary<FieldCategory, PracticeExample> ByCategory, PracticeExample? Default) Parse(string json)
    {
        var map = new Dictionary<FieldCategory, PracticeExample>();
        PracticeExample? fallback = null;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return (map, null);

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (Read(property.Value) is not { } example) continue;

            if (string.Equals(property.Name, DefaultKey, StringComparison.OrdinalIgnoreCase))
                fallback = example;
            else if (ByName<FieldCategory>(property.Name) is { } category)
                map[category] = example;
        }

        return (map, fallback);
    }

    /// <summary>
    /// The example for a field, or the behavioural fallback when the field is unset, unseeded or
    /// <see cref="FieldCategory.Other"/>. Null only when the file itself could not be read.
    /// </summary>
    public PracticeExample? For(FieldCategory? category) =>
        category is { } c && _byCategory.TryGetValue(c, out var example) ? example : _default;

    private static PracticeExample? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        var question = Str(e, "question");
        if (question is null) return null;

        return new PracticeExample(
            question,
            ByName<PracticeDifficulty>(Str(e, "difficulty")) ?? PracticeDifficulty.Medium,
            ByName<QuestionCategory>(Str(e, "category")) ?? QuestionCategory.Technical,
            Str(e, "topic"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Trim().Length > 0
            ? s.Trim()
            : null;

    /// <summary>
    /// Matches an enum member by name only. <c>Enum.TryParse</c> would read "5" as
    /// <see cref="FieldCategory.Healthcare"/>, and a number where a name belongs is a typo, not a choice.
    /// </summary>
    private static T? ByName<T>(string? value) where T : struct, Enum =>
        value is { Length: > 0 } && char.IsLetter(value[0]) &&
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;
}
