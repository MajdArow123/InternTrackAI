using InternTrackAI.Helpers;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

public class ResumeMatchTests
{
    [Fact]
    public void Parses_a_well_formed_response_into_matched_and_missing_skills()
    {
        const string json = """
            {
              "score": 82,
              "matchingSkills": ["C#", "ASP.NET Core", "", "SQL"],
              "missingSkills": ["Kubernetes", null],
              "strengths": ["Shipped a production API"],
              "summary": "Strong overlap on the backend stack."
            }
            """;

        var r = ResumeMatcherService.ParseMatchResponse(json);

        Assert.True(r.Success);
        Assert.Equal(82, r.Score);
        Assert.Equal("APPLY", r.Recommendation);
        Assert.Equal(new[] { "C#", "ASP.NET Core", "SQL" }, r.MatchingSkills);   // blanks dropped
        Assert.Equal(new[] { "Kubernetes" }, r.MissingSkills);                    // nulls dropped
        Assert.Equal(new[] { "Shipped a production API" }, r.Strengths);
        Assert.Equal("Strong overlap on the backend stack.", r.Summary);
        Assert.Null(r.Error);
    }

    [Theory]
    [InlineData(100, "APPLY")]
    [InlineData(80,  "APPLY")]
    [InlineData(79,  "APPLY")]
    [InlineData(60,  "APPLY")]
    [InlineData(59,  "MAYBE")]
    [InlineData(40,  "MAYBE")]
    [InlineData(39,  "CONSIDER SKIPPING")]
    [InlineData(20,  "CONSIDER SKIPPING")]
    [InlineData(19,  "SKIP")]
    [InlineData(0,   "SKIP")]
    public void Recommendation_follows_the_80_60_40_20_bands(int score, string expected)
    {
        Assert.Equal(expected, ResumeMatcherService.RecommendationFor(score));
    }

    [Theory]
    [InlineData(100, 5)]
    [InlineData(80, 5)]
    [InlineData(79, 4)]
    [InlineData(60, 4)]
    [InlineData(59, 3)]
    [InlineData(40, 3)]
    [InlineData(39, 2)]
    [InlineData(20, 2)]
    [InlineData(19, 1)]
    [InlineData(0, 1)]
    public void Ui_match_tier_uses_the_same_bands_as_the_recommendation(int score, int tier)
    {
        Assert.Equal(tier, StatusDisplay.MatchTier(score));
    }

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    public void Score_is_clamped_to_0_100(int raw, int expected)
    {
        var r = ResumeMatcherService.ParseMatchResponse($$"""{"score": {{raw}}}""");
        Assert.True(r.Success);
        Assert.Equal(expected, r.Score);
    }

    [Fact]
    public void Non_numeric_score_and_missing_arrays_degrade_to_zero_and_empty()
    {
        var r = ResumeMatcherService.ParseMatchResponse("""{"score": "eighty", "summary": 42}""");

        Assert.True(r.Success);
        Assert.Equal(0, r.Score);
        Assert.Equal("SKIP", r.Recommendation);
        Assert.Empty(r.MatchingSkills);
        Assert.Empty(r.MissingSkills);
        Assert.Empty(r.Strengths);
        Assert.Null(r.Summary);     // wrong type is treated as absent
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"score\": 50,")]
    public void Unparseable_response_is_a_failed_result_with_a_user_facing_error(string raw)
    {
        var r = ResumeMatcherService.ParseMatchResponse(raw);
        Assert.False(r.Success);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }
}
