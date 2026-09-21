using System.Text.Json;
using System.Text.Json.Serialization;

namespace InternTrackAI.Services;

/// <summary>One superseded attempt at a question: what was said, what it scored, and when.</summary>
/// <remarks>
/// The feedback on a superseded answer is deliberately <b>not</b> kept. What a retry needs is "what did
/// I say last time and did it go better" — the answer and the score — and storing three full critiques
/// alongside three full answers triples a text column for the part a user is least likely to re-read.
/// </remarks>
public sealed record PriorAttempt(
    [property: JsonPropertyName("answer")] string Answer,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("answeredAt")] DateTime AnsweredAt);

/// <summary>
/// Reads and rotates <see cref="Models.PracticeQuestion.PriorAttemptsJson"/>: the last few attempts at
/// a question, newest first, capped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capped at <see cref="Keep"/>.</b> Same reasoning as <c>ResumeParseService</c> pruning parsed
/// drafts to three: an attempt from six tries ago is not something anyone scrolls back to, and an
/// uncapped list in a column is an uncapped column.
/// </para>
/// <para>
/// <b>Nothing here throws on bad input.</b> If the column holds something unreadable — a hand-edited
/// row, a shape from a future version — the history is replaced rather than defended. Losing the record
/// of an old attempt is a small cost; being unable to answer the question at all is not.
/// </para>
/// </remarks>
public static class AttemptHistory
{
    /// <summary>How many superseded attempts survive. The current attempt lives in its own columns.</summary>
    public const int Keep = 3;

    private static readonly JsonSerializerOptions Relaxed = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>Newest first. An empty, null or unreadable column reads as no history.</summary>
    public static List<PriorAttempt> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();

        try
        {
            var attempts = JsonSerializer.Deserialize<List<PriorAttempt>>(json, Relaxed);
            if (attempts is null) return new();

            return attempts
                .Where(a => !string.IsNullOrWhiteSpace(a.Answer))
                .Select(a => a with { Score = Math.Clamp(a.Score, AnswerFeedback.MinScore, AnswerFeedback.MaxScore) })
                .Take(Keep)
                .ToList();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>
    /// Puts <paramref name="attempt"/> at the front and drops anything past <see cref="Keep"/>.
    /// Returns null when there is no history to store, so the column stays null rather than holding
    /// <c>"[]"</c> for every question that has been answered exactly once.
    /// </summary>
    public static string? Append(string? existingJson, PriorAttempt attempt)
    {
        if (string.IsNullOrWhiteSpace(attempt.Answer)) return NullIfEmpty(Read(existingJson));

        var rotated = new List<PriorAttempt> { attempt };
        rotated.AddRange(Read(existingJson));

        return NullIfEmpty(rotated.Take(Keep).ToList());
    }

    private static string? NullIfEmpty(List<PriorAttempt> attempts) =>
        attempts.Count == 0 ? null : JsonSerializer.Serialize(attempts);
}
