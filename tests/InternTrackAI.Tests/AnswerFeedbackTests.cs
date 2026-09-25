using System.Text.Json;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// <see cref="AnswerFeedback.FromJson"/>: what the card is allowed to assume about a model's reply.
/// </summary>
/// <remarks>
/// The contract under test is the one <c>ParsedProfile.FromJson</c> established — <b>never throw</b>. A
/// user who has just typed a paragraph and waited for a round trip should not be shown an error because
/// the model returned five improvements instead of two.
/// </remarks>
public class AnswerFeedbackTests
{
    private const string Full = """
    {
      "score": 4,
      "strengths": ["You named a specific patient scenario.", "Your triage order was defensible."],
      "improvements": ["Say what the outcome was.", "Name the protocol you were following."],
      "missingPoints": ["How you escalated to the attending."],
      "revisedOpening": "On a night shift with two trauma arrivals, I was the only RN on triage."
    }
    """;

    [Fact]
    public void A_complete_reply_parses_field_for_field()
    {
        var f = AnswerFeedback.FromJson(Full);

        Assert.Equal(4, f.Score);
        Assert.Equal(2, f.Strengths.Count);
        Assert.Equal("You named a specific patient scenario.", f.Strengths[0]);
        Assert.Equal(2, f.Improvements.Count);
        Assert.Equal("Say what the outcome was.", f.Improvements[0]);
        Assert.Single(f.MissingPoints);
        Assert.StartsWith("On a night shift", f.RevisedOpening);
    }

    // ── Exactly two improvements, whatever arrives ────────────────────────────

    [Fact]
    public void One_improvement_is_padded_to_two_with_a_blank_the_view_skips()
    {
        // Padded with "" rather than filler text: the card renders one real improvement instead of one
        // real one and one invented one.
        var f = AnswerFeedback.FromJson("""{"score":3,"improvements":["Give a number."]}""");

        Assert.Equal(AnswerFeedback.ImprovementCount, f.Improvements.Count);
        Assert.Equal("Give a number.", f.Improvements[0]);
        Assert.Equal("", f.Improvements[1]);
    }

    [Fact]
    public void Four_improvements_are_truncated_to_two()
    {
        var f = AnswerFeedback.FromJson("""{"score":3,"improvements":["A","B","C","D"]}""");

        Assert.Equal(new[] { "A", "B" }, f.Improvements);
    }

    [Fact]
    public void No_improvements_at_all_still_yields_two_slots()
    {
        var f = AnswerFeedback.FromJson("""{"score":2}""");

        Assert.Equal(AnswerFeedback.ImprovementCount, f.Improvements.Count);
        Assert.All(f.Improvements, i => Assert.Equal("", i));
    }

    // ── Score, clamped ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("9", 5)]
    [InlineData("6", 5)]
    [InlineData("0", 1)]
    [InlineData("-3", 1)]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    public void The_score_is_clamped_to_the_documented_range(string raw, int expected) =>
        Assert.Equal(expected, AnswerFeedback.FromJson($"{{\"score\":{raw}}}").Score);

    [Fact]
    public void A_score_returned_as_a_string_is_read_rather_than_discarded()
    {
        // A model told to return a number sometimes returns "4" or "4/5".
        Assert.Equal(4, AnswerFeedback.FromJson("""{"score":"4"}""").Score);
        Assert.Equal(4, AnswerFeedback.FromJson("""{"score":"4/5"}""").Score);
    }

    [Fact]
    public void An_unreadable_score_reads_as_the_minimum_not_zero()
    {
        // Zero would become a fourth colour on a chip built for three.
        Assert.Equal(AnswerFeedback.MinScore, AnswerFeedback.FromJson("""{"score":"great"}""").Score);
        Assert.Equal(AnswerFeedback.MinScore, AnswerFeedback.FromJson("""{"score":null}""").Score);
        Assert.Equal(AnswerFeedback.MinScore, AnswerFeedback.FromJson("""{}""").Score);
    }

    // ── Never throws ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not json at all")]
    [InlineData("{\"score\": ")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a bare string\"")]
    public void Anything_unreadable_yields_a_usable_object(string? json)
    {
        var f = AnswerFeedback.FromJson(json);

        Assert.Equal(AnswerFeedback.MinScore, f.Score);
        Assert.Empty(f.Strengths);
        Assert.Equal(AnswerFeedback.ImprovementCount, f.Improvements.Count);
        Assert.Null(f.RevisedOpening);
    }

    [Fact]
    public void A_single_string_where_a_list_was_asked_for_is_accepted()
    {
        var f = AnswerFeedback.FromJson("""{"score":3,"strengths":"You were concise."}""");

        Assert.Equal(new[] { "You were concise." }, f.Strengths);
    }

    [Fact]
    public void Non_string_list_members_are_dropped_rather_than_rendered_as_nothing()
    {
        var f = AnswerFeedback.FromJson("""{"score":3,"strengths":["Real",null,7,{"a":1},"Also real"]}""");

        Assert.Equal(new[] { "Real", "Also real" }, f.Strengths);
    }

    [Fact]
    public void Long_bullets_are_truncated_so_one_reply_cannot_dominate_the_card()
    {
        var giant = new string('x', AnswerFeedback.MaxBulletLength + 200);
        var f = AnswerFeedback.FromJson(JsonSerializer.Serialize(new { score = 3, strengths = new[] { giant } }));

        Assert.Single(f.Strengths);
        Assert.True(f.Strengths[0].Length <= AnswerFeedback.MaxBulletLength + 2, f.Strengths[0].Length.ToString());
    }

    [Fact]
    public void Multiline_bullets_are_collapsed_to_one_line()
    {
        var f = AnswerFeedback.FromJson("""{"score":3,"strengths":["Line one.\n\n  Line two."]}""");

        Assert.Equal("Line one. Line two.", f.Strengths[0]);
    }
}
