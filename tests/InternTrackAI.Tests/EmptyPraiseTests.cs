using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// The manufactured-strength guard. Every positive case here is the observed production failure or a
/// close relative of it; the negative cases are what must survive.
/// </summary>
/// <remarks>
/// The bias under test is deliberate and asymmetric: **dropping a real strength is worse than keeping
/// a weak one**, so the guard fires only when the whole sentence is filler. Tests that pin the things
/// it must NOT drop therefore matter more here than the ones that pin what it drops.
/// </remarks>
public class EmptyPraiseTests
{
    // ── The failure this was built from ──────────────────────────────────────

    [Fact]
    public void The_production_failure_is_caught()
    {
        // A junk answer to a linear-vs-non-linear question scored 3/5 and was credited with this.
        // It would be equally true of an answer that said nothing at all.
        Assert.True(EmptyPraise.IsEmpty("You acknowledged that both approaches have their advantages and disadvantages."));
    }

    [Theory]
    [InlineData("You mentioned the pros and cons of each.")]
    [InlineData("Shows an understanding of the topic.")]
    [InlineData("You demonstrate awareness of both methods.")]
    [InlineData("The answer is clear and concise.")]
    [InlineData("It addresses the question.")]
    [InlineData("A reasonable attempt.")]
    [InlineData("You covered the basics.")]
    [InlineData("Good starting point.")]
    [InlineData("You noted that both options have advantages and disadvantages.")]
    public void Praise_that_would_fit_any_answer_is_dropped(string strength) =>
        Assert.True(EmptyPraise.IsEmpty(strength), $"should have been caught: {strength}");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("...")]
    public void Nothing_at_all_counts_as_empty(string? strength) => Assert.True(EmptyPraise.IsEmpty(strength));

    // ── What must survive, which matters more ────────────────────────────────

    [Theory]
    [InlineData("You named the triage protocol you were working to.")]
    [InlineData("You gave a concrete example: the night two patients deteriorated at once.")]
    [InlineData("You said the migration cut query time from 400ms to 90ms.")]
    [InlineData("You explained why you chose Kanban over Scrum for that team.")]
    [InlineData("You escalated to the attending within ten minutes, and said so.")]
    [InlineData("Your answer walks through the airway check before circulation.")]
    public void A_strength_that_names_something_survives(string strength) =>
        Assert.False(EmptyPraise.IsEmpty(strength), $"should have survived: {strength}");

    [Fact]
    public void A_platitude_does_not_poison_a_strength_that_also_says_something()
    {
        // The guard fires only when the WHOLE sentence is filler. "trade-offs" language alongside a
        // real detail is exactly how a good strength reads, and must not be collateral damage.
        Assert.False(EmptyPraise.IsEmpty(
            "You weighed the pros and cons and landed on Postgres because of the JSON column support."));

        Assert.False(EmptyPraise.IsEmpty(
            "You acknowledged both approaches have advantages and disadvantages, then chose linear for the audit trail."));
    }

    [Fact]
    public void Case_and_punctuation_do_not_let_a_platitude_through()
    {
        Assert.True(EmptyPraise.IsEmpty("BOTH APPROACHES HAVE ADVANTAGES AND DISADVANTAGES!"));
        Assert.True(EmptyPraise.IsEmpty("You acknowledged, clearly, that both approaches have advantages and disadvantages..."));
    }

    // ── Filtering a list ─────────────────────────────────────────────────────

    [Fact]
    public void Filter_keeps_the_real_ones_and_drops_the_rest()
    {
        var kept = EmptyPraise.Filter(new[]
        {
            "You acknowledged that both approaches have their advantages and disadvantages.",
            "You named the triage protocol you were working to.",
            "Shows an understanding of the topic.",
        });

        Assert.Equal(new[] { "You named the triage protocol you were working to." }, kept);
    }

    [Fact]
    public void An_answer_with_nothing_working_yields_no_strengths_rather_than_a_fallback()
    {
        // The card then omits "What worked" entirely, which is the truth about that answer and more
        // useful than invented praise.
        Assert.Empty(EmptyPraise.Filter(new[]
        {
            "You acknowledged that both approaches have advantages and disadvantages.",
            "A reasonable attempt.",
        }));
    }

    // ── Through the reader, which is where it actually runs ──────────────────

    [Fact]
    public void FromJson_drops_manufactured_praise_before_the_card_ever_sees_it()
    {
        var feedback = AnswerFeedback.FromJson("""
        {
          "score": 3,
          "strengths": ["You acknowledged that both approaches have their advantages and disadvantages.",
                        "You named the audit trail as the reason you'd pick linear."],
          "improvements": ["Give a number.", "Name the team."],
          "missingPoints": [],
          "revisedOpening": "For an audit trail, I'd take linear."
        }
        """);

        Assert.Equal(new[] { "You named the audit trail as the reason you'd pick linear." }, feedback.Strengths);
    }

    [Fact]
    public void A_reply_of_nothing_but_platitudes_leaves_the_strengths_empty()
    {
        var feedback = AnswerFeedback.FromJson("""
        {"score":2,"strengths":["Shows an understanding of the topic.","A good starting point."],
         "improvements":["Give a specific example.","Say what you actually did."]}
        """);

        Assert.Empty(feedback.Strengths);
        Assert.Equal(2, feedback.Improvements.Count);   // the rest of the card is untouched
    }
}
