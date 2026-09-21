using System.Text;
using System.Text.Json;

namespace InternTrackAI.Services;

/// <summary>
/// The model's verdict on one practice answer, bounded and shaped so the card that renders it does not
/// have to defend itself against the reply.
/// </summary>
/// <remarks>
/// <para>
/// Stored as JSON in <see cref="Models.PracticeQuestion.AiFeedback"/>. <see cref="Score"/> is
/// duplicated into its own integer column because Phase 5's progress panel has to aggregate it in SQL,
/// and a number inside a JSON string is not something to ask either provider to sum.
/// </para>
/// <para>
/// <b>Exactly two improvements</b>, padded or truncated on read. The prompt asks for two; this is what
/// makes it true. A card whose shape is at the model's discretion is a card that looks broken the day
/// the model returns five bullets.
/// </para>
/// </remarks>
public sealed record AnswerFeedback(
    int Score,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Improvements,
    IReadOnlyList<string> MissingPoints,
    string? RevisedOpening)
{
    public const int MinScore = 1;
    public const int MaxScore = 5;

    /// <summary>How many improvements a card renders. Not a maximum — an exact count.</summary>
    public const int ImprovementCount = 2;

    public const int MaxStrengths = 4;
    public const int MaxMissingPoints = 4;
    public const int MaxBulletLength = 300;
    public const int MaxOpeningLength = 600;

    /// <summary>
    /// Reads the model's reply. <b>Never throws</b> — same contract as
    /// <see cref="ParsedProfile.FromJson"/>: a reply that is short, misspelled or not JSON at all
    /// degrades to a thinner card, because a user who has just typed a paragraph should not be shown a
    /// stack trace over the model's formatting.
    /// </summary>
    public static AnswerFeedback FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty();

        JsonElement r;
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(json);
            r = doc.RootElement;
        }
        catch (JsonException)
        {
            doc?.Dispose();
            return Empty();
        }

        using (doc)
        {
            if (r.ValueKind != JsonValueKind.Object) return Empty();

            return new AnswerFeedback(
                ReadScore(r),
                // Filtered, not just read: the prompt asks for no invented praise and this is what
                // makes it true. See EmptyPraise for the observed failure it was built from.
                EmptyPraise.Filter(Bullets(r, "strengths", MaxStrengths)),
                Exactly(Bullets(r, "improvements", ImprovementCount)),
                Bullets(r, "missingPoints", MaxMissingPoints),
                Truncate(PromptData.OneLine(Str(r, "revisedOpening")), MaxOpeningLength));
        }
    }

    /// <summary>A usable object with nothing in it, so every caller has a non-null feedback to render.</summary>
    private static AnswerFeedback Empty() =>
        new(MinScore, Array.Empty<string>(), Exactly(Array.Empty<string>()), Array.Empty<string>(), null);

    /// <summary>
    /// Clamped to 1–5, the way <see cref="ResumeScoreService"/> clamps its 0–100. A missing or
    /// unreadable score reads as <see cref="MinScore"/> rather than 0: the column is documented as
    /// 1–5, and a 0 would quietly become a fourth colour on the chip.
    /// </summary>
    private static int ReadScore(JsonElement root)
    {
        if (!root.TryGetProperty("score", out var v)) return MinScore;

        var raw = v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            // A model that has been told to return a number sometimes returns "4" or "4/5".
            JsonValueKind.String => ParseLeadingInt(v.GetString()),
            _ => (int?)null
        };

        return raw is { } score ? Math.Clamp(score, MinScore, MaxScore) : MinScore;
    }

    private static int? ParseLeadingInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = new string(text.TrimStart().TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Pads to or truncates at <see cref="ImprovementCount"/>. Padding with an empty string rather than
    /// filler text: the view skips blanks, so a stingy reply shows one improvement instead of one real
    /// one and one invented one.
    /// </summary>
    private static IReadOnlyList<string> Exactly(IReadOnlyList<string> improvements)
    {
        if (improvements.Count == ImprovementCount) return improvements;

        var fixedUp = improvements.Take(ImprovementCount).ToList();
        while (fixedUp.Count < ImprovementCount) fixedUp.Add("");
        return fixedUp;
    }

    private static IReadOnlyList<string> Bullets(JsonElement root, string key, int max)
    {
        if (!root.TryGetProperty(key, out var v)) return Array.Empty<string>();

        // Tolerates a single string where an array was asked for — a common slip that loses nothing.
        var items = v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()),
            JsonValueKind.String => new[] { v.GetString() },
            _ => Enumerable.Empty<string?>()
        };

        return items
            .Select(s => Truncate(PromptData.OneLine(s), MaxBulletLength))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// The three-part plain-text rendering the interview prep page has always shown. Kept so
    /// <c>InterviewPrepController.CritiqueAnswer</c> can go through this one service without its
    /// inline script — deferred to its own change — needing to learn a new response shape.
    /// </summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();

        if (Strengths.Count > 0)
            sb.Append("What works: ").Append(string.Join(" ", Strengths)).Append("\n\n");

        var real = Improvements.Where(i => i.Length > 0).ToList();
        if (real.Count > 0)
            sb.Append("What to change: ").Append(string.Join(" ", real)).Append("\n\n");

        if (MissingPoints.Count > 0)
            sb.Append("What's missing: ").Append(string.Join(" ", MissingPoints)).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(RevisedOpening))
            sb.Append("A stronger opening: ").Append(RevisedOpening);

        var text = sb.ToString().TrimEnd();
        return text.Length > 0 ? $"Score: {Score}/{MaxScore}\n\n{text}" : $"Score: {Score}/{MaxScore}";
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + " …";
    }

    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
