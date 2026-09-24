using InternTrackAI.Models;
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

    /// <summary>
    /// The window behind <see cref="AnsweredRecently"/>: the last seven of the user's local days,
    /// today included. Rolling rather than "since Monday" so the figure does not drop to zero at the
    /// start of every week and read as a slump.
    /// </summary>
    public const int RecentActivityDays = 7;

    /// <summary>True when there is nothing to show. The view skips the card entirely rather than render zeros.</summary>
    public bool IsEmpty => Total == 0;

    /// <summary>
    /// Answers given since the cutoff passed to <see cref="Build"/>; null when none was passed. The
    /// cutoff is the caller's so the time-zone decision stays at the edge and this stays pure.
    /// </summary>
    public int? AnsweredRecently { get; init; }

    /// <summary>
    /// The UTC instant <see cref="RecentActivityDays"/> local days ago began, for the user's own zone —
    /// so "the last 7 days" means their days, not UTC's.
    /// </summary>
    public static DateTime RecentActivityCutoffUtc(UserClock clock) =>
        clock.StartOfLocalDayUtc(clock.Today.AddDays(-(RecentActivityDays - 1)));

    public static readonly PracticeProgress None =
        new(0, 0, null, null, null, null, null, Array.Empty<DifficultyCoverage>());

    public static PracticeProgress Build(IReadOnlyList<PracticeProgressRow> rows, DateTime? recentSinceUtc = null)
    {
        if (rows.Count == 0) return None with { AnsweredRecently = recentSinceUtc is null ? null : 0 };

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
            return None with { Total = rows.Count, Coverage = coverage, AnsweredRecently = recentSinceUtc is null ? null : 0 };

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
            Coverage: coverage)
        {
            AnsweredRecently = recentSinceUtc is { } since ? answered.Count(r => r.AnsweredAt >= since) : null
        };
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

/// <summary>
/// The one query behind every <see cref="PracticeProgress"/>: five narrow columns, every question the
/// user has, unfiltered. Shared by the practice page and the dashboard so the two cards cannot count
/// different things.
/// </summary>
public static class PracticeProgressRows
{
    public static IQueryable<PracticeProgressRow> ProgressRowsFor(this IQueryable<PracticeQuestion> questions, string userId) =>
        questions
            .Where(q => q.UserId == userId)
            .Select(q => new PracticeProgressRow(q.Id, q.Difficulty, q.Score, q.Topic, q.AnsweredAt));
}
