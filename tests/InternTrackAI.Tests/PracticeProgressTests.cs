using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// The progress card's arithmetic. Pure, so every number on the panel is pinned without a host — and
/// the two numbers most likely to be quietly wrong (the trend window and the weakest topic) get the
/// most attention here.
/// </summary>
public class PracticeProgressTests
{
    private static readonly DateTime Base = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private static PracticeProgressRow Row(
        int id,
        int? score = null,
        string? topic = null,
        PracticeDifficulty difficulty = PracticeDifficulty.Medium,
        int? minutesAgo = null) =>
        new(id, difficulty, score, topic, score is null && minutesAgo is null ? null : Base.AddMinutes(-(minutesAgo ?? id)));

    /// <summary>n answers, newest last by id, all on one topic unless told otherwise.</summary>
    private static List<PracticeProgressRow> Answers(params int[] scores) =>
        scores.Select((s, i) => Row(i + 1, s, "hash maps", minutesAgo: scores.Length - i)).ToList();

    // ── Nothing to show ──────────────────────────────────────────────────────

    [Fact]
    public void An_empty_set_is_empty_rather_than_a_card_of_zeros()
    {
        var p = PracticeProgress.Build(Array.Empty<PracticeProgressRow>());

        Assert.True(p.IsEmpty);
        Assert.Equal(0, p.Total);
        Assert.Null(p.AverageScore);
        Assert.Empty(p.Coverage);
    }

    [Fact]
    public void Questions_with_no_answers_yet_still_report_totals_and_coverage()
    {
        var p = PracticeProgress.Build(new List<PracticeProgressRow>
        {
            Row(1, difficulty: PracticeDifficulty.Easy),
            Row(2, difficulty: PracticeDifficulty.Hard),
        });

        Assert.False(p.IsEmpty);
        Assert.Equal(2, p.Total);
        Assert.Equal(0, p.Answered);
        Assert.Null(p.AverageScore);          // nothing to average, not zero
        Assert.Null(p.WeakestTopic);
        Assert.All(p.Coverage, c => Assert.Equal(0, c.Answered));
    }

    [Fact]
    public void A_row_answered_without_a_score_does_not_drag_the_average_down()
    {
        // AnsweredAt set but Score null only happens if something went wrong; counting it as a zero
        // would be the worst possible reading of it.
        var rows = Answers(4, 4);
        rows.Add(new PracticeProgressRow(99, PracticeDifficulty.Medium, null, "hash maps", Base));

        var p = PracticeProgress.Build(rows);

        Assert.Equal(2, p.Answered);
        Assert.Equal(4, p.AverageScore);
    }

    // ── Averages ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_average_is_over_answers_not_over_questions()
    {
        var rows = Answers(2, 4);
        rows.Add(Row(50));                 // generated, never answered

        var p = PracticeProgress.Build(rows);

        Assert.Equal(3, p.Total);
        Assert.Equal(2, p.Answered);
        Assert.Equal(3, p.AverageScore);
    }

    [Fact]
    public void The_recent_average_covers_only_the_last_window()
    {
        // 12 answers: the first two are 1s, the last ten are 5s.
        var p = PracticeProgress.Build(Answers(1, 1, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5));

        Assert.Equal(5, p.RecentAverage);
        Assert.Equal(4.333, Math.Round(p.AverageScore!.Value, 3));
    }

    [Fact]
    public void Answers_sharing_a_timestamp_are_ordered_by_id_so_the_window_is_stable()
    {
        // Every answer at the same instant; only the id can decide which ten are "recent".
        var rows = Enumerable.Range(1, 12)
            .Select(i => new PracticeProgressRow(i, PracticeDifficulty.Medium, i <= 2 ? 1 : 5, "hash maps", Base))
            .ToList();

        var p = PracticeProgress.Build(rows);

        Assert.Equal(5, p.RecentAverage);   // ids 12..3, all 5s
    }

    // ── The trend, and when there isn't one ──────────────────────────────────

    [Fact]
    public void No_trend_is_drawn_under_two_full_windows()
    {
        var p = PracticeProgress.Build(Answers(Enumerable.Repeat(3, PracticeProgress.MinAnswersForTrend - 1).ToArray()));

        Assert.Equal(PracticeProgress.MinAnswersForTrend - 1, p.Answered);
        Assert.Null(p.TrendDelta);
        Assert.NotNull(p.RecentAverage);    // the number still stands alone
    }

    [Fact]
    public void An_improving_run_gives_a_positive_delta_at_exactly_two_windows()
    {
        // Oldest ten 2s, newest ten 4s.
        var scores = Enumerable.Repeat(2, 10).Concat(Enumerable.Repeat(4, 10)).ToArray();

        var p = PracticeProgress.Build(Answers(scores));

        Assert.Equal(PracticeProgress.MinAnswersForTrend, p.Answered);
        Assert.Equal(2, p.TrendDelta);
        Assert.Equal(4, p.RecentAverage);
    }

    [Fact]
    public void A_slump_gives_a_negative_delta()
    {
        var scores = Enumerable.Repeat(5, 10).Concat(Enumerable.Repeat(3, 10)).ToArray();

        Assert.Equal(-2, PracticeProgress.Build(Answers(scores)).TrendDelta);
    }

    [Fact]
    public void The_trend_compares_the_last_two_windows_not_the_whole_history()
    {
        // 30 answers: 1s, then 5s, then 3s. Against all history this looks like improvement; against
        // the previous window it is a decline, which is the honest reading of "lately".
        var scores = Enumerable.Repeat(1, 10)
            .Concat(Enumerable.Repeat(5, 10))
            .Concat(Enumerable.Repeat(3, 10)).ToArray();

        var p = PracticeProgress.Build(Answers(scores));

        Assert.Equal(-2, p.TrendDelta);
        Assert.Equal(3, p.RecentAverage);
    }

    // ── Weakest topic ────────────────────────────────────────────────────────

    [Fact]
    public void A_topic_under_the_minimum_is_not_called_the_weakest()
    {
        // "recursion" averages 1 but has only two answers; one bad answer is not a weakness.
        var rows = new List<PracticeProgressRow>
        {
            Row(1, 1, "recursion", minutesAgo: 5),
            Row(2, 1, "recursion", minutesAgo: 4),
            Row(3, 3, "hash maps", minutesAgo: 3),
            Row(4, 3, "hash maps", minutesAgo: 2),
            Row(5, 3, "hash maps", minutesAgo: 1),
        };

        var p = PracticeProgress.Build(rows);

        Assert.Equal("hash maps", p.WeakestTopic);
        Assert.Equal(3, p.WeakestTopicAverage);
    }

    [Fact]
    public void Padded_spellings_of_one_topic_are_a_single_average_not_two_thin_ones()
    {
        // The reuse that matters. These three differ only by filler TopicKey drops ("techniques",
        // "for"), so they collapse to one key — the topic clears the 3-answer minimum instead of
        // splitting into three groups that each fall under it. Real collisions of this exact shape came
        // out of the live generation runs (CLAUDE.md §8).
        var rows = new List<PracticeProgressRow>
        {
            Row(1, 1, "SQL query optimization",             minutesAgo: 3),
            Row(2, 1, "SQL query optimization techniques",  minutesAgo: 2),
            Row(3, 1, "Optimization techniques for SQL query", minutesAgo: 1),
            Row(4, 5, "hash maps",                          minutesAgo: 9),
            Row(5, 5, "hash maps",                          minutesAgo: 8),
            Row(6, 5, "hash maps",                          minutesAgo: 7),
        };

        var p = PracticeProgress.Build(rows);

        Assert.Equal(1, p.WeakestTopicAverage);
        // Labelled with the newest spelling, because that is the one the user last saw.
        Assert.Equal("Optimization techniques for SQL query", p.WeakestTopic);
    }

    [Fact]
    public void A_stemming_difference_does_not_collapse_two_topics()
    {
        // The boundary, pinned so nobody assumes more than TopicKey does. It compares exact tokens
        // after dropping a short filler list: "query" and "queries" are different tokens, so these stay
        // two topics. That is the same deliberate limit QuestionHash has on paraphrase — cheap and
        // predictable beats clever and surprising.
        var rows = new List<PracticeProgressRow>
        {
            Row(1, 1, "SQL query optimization",   minutesAgo: 6),
            Row(2, 1, "SQL query optimization",   minutesAgo: 5),
            Row(3, 5, "Optimization of SQL queries", minutesAgo: 4),
            Row(4, 5, "Optimization of SQL queries", minutesAgo: 3),
            Row(5, 5, "Optimization of SQL queries", minutesAgo: 2),
        };

        var p = PracticeProgress.Build(rows);

        // The 1s never reach three answers on their own, so the weakest is the group that does.
        Assert.Equal("Optimization of SQL queries", p.WeakestTopic);
        Assert.Equal(5, p.WeakestTopicAverage);
    }

    [Fact]
    public void Questions_with_no_topic_are_ignored_rather_than_grouped_as_blank()
    {
        var rows = new List<PracticeProgressRow>
        {
            Row(1, 1, "",    minutesAgo: 4),
            Row(2, 1, null,  minutesAgo: 3),
            Row(3, 1, "   ", minutesAgo: 2),
            Row(4, 4, "hash maps", minutesAgo: 9),
            Row(5, 4, "hash maps", minutesAgo: 8),
            Row(6, 4, "hash maps", minutesAgo: 7),
        };

        var p = PracticeProgress.Build(rows);

        Assert.Equal("hash maps", p.WeakestTopic);
    }

    [Fact]
    public void No_topic_clears_the_minimum_means_no_weakest_topic()
    {
        var p = PracticeProgress.Build(Answers(1, 2));

        Assert.Equal(2, p.Answered);
        Assert.Null(p.WeakestTopic);
        Assert.Null(p.WeakestTopicAverage);
    }

    // ── Coverage by difficulty ───────────────────────────────────────────────

    [Fact]
    public void Coverage_reports_every_difficulty_even_the_untouched_ones()
    {
        var rows = new List<PracticeProgressRow>
        {
            Row(1, 4, "a", PracticeDifficulty.Easy, minutesAgo: 3),
            Row(2, null, "b", PracticeDifficulty.Easy),
            Row(3, 2, "c", PracticeDifficulty.Hard, minutesAgo: 1),
        };

        var p = PracticeProgress.Build(rows);

        Assert.Equal(3, p.Coverage.Count);   // Easy, Medium, Hard — Medium included at zero

        var easy = p.Coverage.Single(c => c.Difficulty == PracticeDifficulty.Easy);
        Assert.Equal(2, easy.Total);
        Assert.Equal(1, easy.Answered);
        Assert.Equal(50, easy.Percent);

        var medium = p.Coverage.Single(c => c.Difficulty == PracticeDifficulty.Medium);
        Assert.Equal(0, medium.Total);
        Assert.Equal(0, medium.Percent);     // no questions is 0%, not a divide by zero
    }

    [Fact]
    public void Coverage_percent_rounds_rather_than_truncating()
    {
        var rows = Enumerable.Range(1, 3)
            .Select(i => Row(i, i == 1 ? 4 : null, "a", PracticeDifficulty.Hard, minutesAgo: i))
            .ToList();

        var hard = PracticeProgress.Build(rows).Coverage.Single(c => c.Difficulty == PracticeDifficulty.Hard);

        Assert.Equal(33, hard.Percent);      // 1 of 3
    }
}
