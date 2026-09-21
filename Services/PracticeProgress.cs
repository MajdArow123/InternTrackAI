using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services;

/// <summary>One question, reduced to what the progress panel needs. Projected straight from the query.</summary>
public sealed record PracticeProgressRow(
    int Id,
    PracticeDifficulty Difficulty,
    int? Score,
    string? Topic,
    DateTime? AnsweredAt);

/// <summary>How many questions exist at one difficulty, and how many have been answered.</summary>
public sealed record DifficultyCoverage(PracticeDifficulty Difficulty, int Total, int Answered)
{
    /// <summary>Answered share, 0–100, for the bar's width. Zero questions is 0%, never a divide by zero.</summary>
    public int Percent => Total == 0 ? 0 : (int)Math.Round(Answered * 100.0 / Total);
}

/// <summary>
/// The practice page's progress card: what has been answered, how it is going, and where it is going
/// worst. <b>No model call</b> — this renders on every page load.
/// </summary>
/// <remarks>
/// <para>
/// <b>Computed in C#, not as SQL aggregates, on purpose.</b> Two of these numbers are exactly the
/// shapes this repo has been burned by across providers: the weakest topic is a <c>GROUP BY</c> over a
/// text column, whose answer depends on a collation that is <c>C</c> locally and <c>en_US.utf8</c> in
/// production (CLAUDE.md §8), and the average is <c>avg()</c> over a nullable int, which SQLite and
/// Npgsql do not return as the same type. A parity test could not see the first of those, because the
/// local server is not the production one. Aggregating in memory removes both questions instead of
/// testing for them, and it is what the other analytics here already do —
/// <see cref="ResumeAnalyticsService"/> reads rows then averages, <see cref="SkillGapService"/> groups
/// with an ordinal comparer.
/// </para>
/// </remarks>
public sealed record PracticeProgress(
    int Total,
    int Answered,
    double? AverageScore,
    double? RecentAverage,
    double? TrendDelta,
    string? WeakestTopic,
    double? WeakestTopicAverage,
    IReadOnlyList<DifficultyCoverage> Coverage)
{
    /// <summary>How many recent answers the "lately" average is taken over.</summary>
    public const int RecentWindow = 10;

    /// <summary>
    /// Answers needed before a trend is drawn at all: two full windows. Below this there is no honest
    /// comparison to make, and an arrow off three answers is noise presented as a finding.
    /// </summary>
    public const int MinAnswersForTrend = RecentWindow * 2;

    /// <summary>Answers a topic needs before it can be called the weakest. One bad answer is not a weakness.</summary>
    public const int MinAnswersForWeakestTopic = 3;

    /// <summary>True when there is nothing to show. The view skips the card entirely rather than render zeros.</summary>
    public bool IsEmpty => Total == 0;

    public static readonly PracticeProgress None =
        new(0, 0, null, null, null, null, null, Array.Empty<DifficultyCoverage>());

    public static PracticeProgress Build(IReadOnlyList<PracticeProgressRow> rows)
    {
        if (rows.Count == 0) return None;

        // An answer is one with both a timestamp and a score: a row can carry AnsweredAt with a null
        // Score only if something went wrong, and it must not drag an average toward zero.
        var answered = rows
            .Where(r => r.AnsweredAt is not null && r.Score is not null)
            .OrderByDescending(r => r.AnsweredAt)
            .ThenByDescending(r => r.Id)          // two answers can share a timestamp
            .ToList();

        var coverage = Enum.GetValues<PracticeDifficulty>()
            .Select(d => new DifficultyCoverage(
                d,
                rows.Count(r => r.Difficulty == d),
                answered.Count(r => r.Difficulty == d)))
            .ToList();

        if (answered.Count == 0)
            return None with { Total = rows.Count, Coverage = coverage };

        var scores = answered.Select(r => r.Score!.Value).ToList();
        var recent = scores.Take(RecentWindow).ToList();

        // Only with two full windows behind it. Older-first would compare a full window against a
        // partial one and read as a slump that is really just a short history.
        double? delta = scores.Count >= MinAnswersForTrend
            ? recent.Average() - scores.Skip(RecentWindow).Take(RecentWindow).Average()
            : null;

        var (weakest, weakestAverage) = FindWeakestTopic(answered);

        return new PracticeProgress(
            Total: rows.Count,
            Answered: answered.Count,
            AverageScore: scores.Average(),
            RecentAverage: recent.Average(),
            TrendDelta: delta,
            WeakestTopic: weakest,
            WeakestTopicAverage: weakestAverage,
            Coverage: coverage);
    }

    /// <summary>
    /// The lowest-scoring topic with at least <see cref="MinAnswersForWeakestTopic"/> answers.
    /// </summary>
    /// <remarks>
    /// Grouped by <see cref="TopicKey.Of"/>, not raw text, so "SQL query optimization" and "SQL query
    /// optimization techniques" are one topic with one average rather than two thin ones — the same
    /// normalisation the generator dedupes with, and ordinal, so no collation is involved.
    /// <see cref="TopicKey.Of"/> is the right primitive here and <see cref="TopicKey.Collides"/> is not:
    /// subset containment is not transitive, so it cannot partition anything into groups.
    /// The label shown is the newest original spelling, because that is what the user last saw.
    /// </remarks>
    private static (string? Topic, double? Average) FindWeakestTopic(List<PracticeProgressRow> answeredNewestFirst)
    {
        var weakest = answeredNewestFirst
            .Where(r => !string.IsNullOrWhiteSpace(r.Topic))
            .GroupBy(r => TopicKey.Of(r.Topic), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0 && g.Count() >= MinAnswersForWeakestTopic)
            .Select(g => new
            {
                Label   = g.First().Topic!,        // newest, since the list is newest-first
                Average = g.Average(r => r.Score!.Value)
            })
            .OrderBy(g => g.Average)
            .ThenBy(g => g.Label, StringComparer.Ordinal)   // deterministic on a tie
            .FirstOrDefault();

        return weakest is null ? (null, null) : (weakest.Label, weakest.Average);
    }
}
