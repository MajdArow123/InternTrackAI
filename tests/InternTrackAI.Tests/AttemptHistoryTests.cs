using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// The retry history: rotation, the cap, and what happens when the column holds something unexpected.
/// </summary>
public class AttemptHistoryTests
{
    private static PriorAttempt Attempt(string answer, int score = 3, int daysAgo = 0) =>
        new(answer, score, new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo));

    // ── Reading ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_column_reads_as_no_history(string? json) => Assert.Empty(AttemptHistory.Read(json));

    [Fact]
    public void Unreadable_history_reads_as_none_rather_than_throwing()
    {
        // A hand-edited row or a shape from a future version. Losing the record of an old attempt is a
        // small cost; being unable to answer the question at all is not.
        Assert.Empty(AttemptHistory.Read("not json"));
        Assert.Empty(AttemptHistory.Read("{\"answer\":\"a single object, not a list\"}"));
        Assert.Empty(AttemptHistory.Read("[1,2,3]"));
    }

    [Fact]
    public void A_stored_score_outside_the_range_is_clamped_on_read()
    {
        var read = AttemptHistory.Read("""[{"answer":"An answer long enough to matter.","score":99,"answeredAt":"2026-09-01T00:00:00Z"}]""");

        Assert.Equal(AnswerFeedback.MaxScore, Assert.Single(read).Score);
    }

    [Fact]
    public void An_entry_with_no_answer_is_dropped_rather_than_shown_as_a_blank_card()
    {
        var read = AttemptHistory.Read("""[{"answer":"","score":3,"answeredAt":"2026-09-01T00:00:00Z"},{"answer":"Real.","score":4,"answeredAt":"2026-09-02T00:00:00Z"}]""");

        Assert.Equal("Real.", Assert.Single(read).Answer);
    }

    // ── Rotation ─────────────────────────────────────────────────────────────

    [Fact]
    public void Appending_to_nothing_stores_one_attempt()
    {
        var json = AttemptHistory.Append(null, Attempt("First attempt."));

        var read = AttemptHistory.Read(json);
        Assert.Equal("First attempt.", Assert.Single(read).Answer);
    }

    [Fact]
    public void The_newest_attempt_goes_first()
    {
        var json = AttemptHistory.Append(null, Attempt("Older.", daysAgo: 2));
        json = AttemptHistory.Append(json, Attempt("Newer.", daysAgo: 1));

        var read = AttemptHistory.Read(json);
        Assert.Equal(new[] { "Newer.", "Older." }, read.Select(a => a.Answer));
    }

    [Fact]
    public void The_oldest_attempt_falls_off_once_the_cap_is_reached()
    {
        string? json = null;
        for (var i = 1; i <= AttemptHistory.Keep + 2; i++)
            json = AttemptHistory.Append(json, Attempt($"Attempt {i}."));

        var read = AttemptHistory.Read(json);

        Assert.Equal(AttemptHistory.Keep, read.Count);
        Assert.Equal("Attempt 5.", read[0].Answer);       // newest kept
        Assert.Equal("Attempt 3.", read[^1].Answer);      // oldest kept
        Assert.DoesNotContain("Attempt 1.", read.Select(a => a.Answer));
    }

    [Fact]
    public void Appending_onto_unreadable_history_replaces_it_and_keeps_the_new_attempt()
    {
        var json = AttemptHistory.Append("{{{ not json", Attempt("The one that matters."));

        Assert.Equal("The one that matters.", Assert.Single(AttemptHistory.Read(json)).Answer);
    }

    [Fact]
    public void Nothing_to_store_leaves_the_column_null_rather_than_an_empty_array()
    {
        // Otherwise every question answered exactly once carries "[]" forever.
        Assert.Null(AttemptHistory.Append(null, Attempt("")));
        Assert.Null(AttemptHistory.Append(null, Attempt("   ")));
    }

    [Fact]
    public void A_round_trip_keeps_the_score_and_the_timestamp()
    {
        var attempt = Attempt("An answer with enough substance to grade.", score: 2);

        var read = Assert.Single(AttemptHistory.Read(AttemptHistory.Append(null, attempt)));

        Assert.Equal(2, read.Score);
        Assert.Equal(attempt.AnsweredAt, read.AnsweredAt);
    }
}
