using InternTrackAI.Models.Enums;
using InternTrackAI.Services.Gmail;

namespace InternTrackAI.Tests;

/// <summary>The strict JSON contract between the model and the sync: only three statuses, confidence in 0..1.</summary>
public class StatusClassifierParseTests
{
    [Fact]
    public void Well_formed_answer_is_read_in_full()
    {
        var r = OpenAiStatusClassifier.Parse("""{"matches":true,"suggestedStatus":"Interview","confidence":0.82,"summary":"Stripe invites you to a phone screen next Tuesday.","interviewAt":"2026-09-22T14:00:00-04:00"}""");

        Assert.NotNull(r);
        Assert.True(r!.Matches);
        Assert.Equal(ApplicationStatus.Interview, r.SuggestedStatus);
        Assert.Equal(0.82, r.Confidence, 3);
        Assert.Equal("Stripe invites you to a phone screen next Tuesday.", r.Summary);
        Assert.Equal(new DateTime(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc), r.InterviewAt);
        Assert.Equal(DateTimeKind.Utc, r.InterviewAt!.Value.Kind);
    }

    [Theory]
    [InlineData("Interview", ApplicationStatus.Interview)]
    [InlineData("offer", ApplicationStatus.Offer)]
    [InlineData(" REJECTED ", ApplicationStatus.Rejected)]
    [InlineData("Screening", null)]
    [InlineData("Applied", null)]
    [InlineData("Saved", null)]
    [InlineData("Offer accepted", null)]
    [InlineData("", null)]
    public void Only_interview_offer_and_rejected_are_accepted(string raw, ApplicationStatus? expected)
    {
        var r = OpenAiStatusClassifier.Parse($$$"""{"matches":true,"suggestedStatus":"{{{raw}}}","confidence":0.9,"summary":"x"}""");
        Assert.Equal(expected, r!.SuggestedStatus);
    }

    [Fact]
    public void Non_string_or_missing_status_is_null()
    {
        Assert.Null(OpenAiStatusClassifier.Parse("""{"matches":true,"suggestedStatus":null,"confidence":0.5}""")!.SuggestedStatus);
        Assert.Null(OpenAiStatusClassifier.Parse("""{"matches":true,"suggestedStatus":2,"confidence":0.5}""")!.SuggestedStatus);
        Assert.Null(OpenAiStatusClassifier.Parse("""{"matches":true}""")!.SuggestedStatus);
    }

    [Theory]
    [InlineData("1.7", 1.0)]
    [InlineData("-0.2", 0.0)]
    [InlineData("0.35", 0.35)]
    [InlineData("\"0.6\"", 0.6)]
    [InlineData("\"high\"", 0.0)]
    [InlineData("null", 0.0)]
    public void Confidence_is_clamped_to_the_unit_interval(string rawJson, double expected)
    {
        var r = OpenAiStatusClassifier.Parse($$$"""{"matches":true,"suggestedStatus":"Offer","confidence":{{{rawJson}}},"summary":"x"}""");
        Assert.Equal(expected, r!.Confidence, 3);
    }

    [Fact]
    public void Matches_must_be_literally_true()
    {
        Assert.False(OpenAiStatusClassifier.Parse("""{"matches":"true","suggestedStatus":"Offer"}""")!.Matches);
        Assert.False(OpenAiStatusClassifier.Parse("""{"suggestedStatus":"Offer"}""")!.Matches);
    }

    [Fact]
    public void Summary_is_whitespace_collapsed_and_capped()
    {
        var longText = string.Join(" ", Enumerable.Repeat("word", 120));
        var r = OpenAiStatusClassifier.Parse($$$"""{"matches":true,"summary":"  line one\n\n   line   two  "}""");
        Assert.Equal("line one line two", r!.Summary);
        var capped = OpenAiStatusClassifier.Parse($$$"""{"matches":true,"summary":"{{{longText}}}"}""");
        Assert.True(capped!.Summary.Length <= OpenAiStatusClassifier.SummaryMaxLength);
        Assert.EndsWith("…", capped.Summary);
    }

    [Theory]
    [InlineData("2026-09-22T14:00:00Z", DateTimeKind.Utc)]
    [InlineData("2026-09-22T14:00:00+02:00", DateTimeKind.Utc)]
    [InlineData("2026-09-22T14:00:00", DateTimeKind.Unspecified)]
    [InlineData("2026-09-22T14:00", DateTimeKind.Unspecified)]
    public void Interview_time_keeps_utc_when_an_offset_is_given_and_is_local_otherwise(string raw, DateTimeKind kind)
    {
        var parsed = OpenAiStatusClassifier.ParseInterviewAt(raw);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Value.Kind);
    }

    [Theory]
    [InlineData("2026-09-22")]       // a date alone is not an interview time
    [InlineData("next Tuesday")]
    [InlineData("")]
    [InlineData(null)]
    public void Unusable_interview_times_are_dropped(string? raw) => Assert.Null(OpenAiStatusClassifier.ParseInterviewAt(raw));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"Interview\"")]
    public void Garbage_is_null_not_an_exception(string raw) => Assert.Null(OpenAiStatusClassifier.Parse(raw));
}
