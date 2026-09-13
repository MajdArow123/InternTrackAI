using InternTrackAI.Services;
using Microsoft.Extensions.Configuration;

namespace InternTrackAI.Tests;

public class DemoResetServiceTests
{
    private static IConfiguration Config(params (string, string?)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(x => x.Item1, x => x.Item2)).Build();

    [Fact]
    public void Disabled_by_default_even_when_demo_email_is_set()
    {
        Assert.False(DemoResetService.IsEnabled(Config(("Demo:Email", "demo@example.com"))));
    }

    [Fact]
    public void Disabled_when_auto_reset_is_true_but_email_is_missing()
    {
        Assert.False(DemoResetService.IsEnabled(Config(("Demo:AutoReset", "true"))));
    }

    [Fact]
    public void Enabled_only_when_both_switches_are_on()
    {
        Assert.True(DemoResetService.IsEnabled(Config(("Demo:AutoReset", "true"), ("Demo:Email", "demo@example.com"))));
    }

    [Theory]
    [InlineData(null, 4, 0)]
    [InlineData("garbage", 4, 0)]
    [InlineData("04:00", 4, 0)]
    [InlineData("23:30", 23, 30)]
    public void Reset_time_parses_or_falls_back_to_0400(string? raw, int hour, int minute)
    {
        var cfg = raw == null ? Config() : Config(("Demo:ResetTimeUtc", raw));
        Assert.Equal(new TimeOnly(hour, minute), DemoResetService.ResetTime(cfg));
    }

    [Fact]
    public void Next_run_is_later_today_when_the_time_has_not_passed()
    {
        var now = new DateTimeOffset(2026, 9, 13, 1, 15, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 4, 0, 0, TimeSpan.Zero), DemoResetService.NextRun(now, new TimeOnly(4, 0)));
    }

    [Fact]
    public void Next_run_rolls_to_tomorrow_once_the_time_has_passed()
    {
        var now = new DateTimeOffset(2026, 9, 13, 4, 0, 0, TimeSpan.Zero);   // exactly at the boundary → tomorrow
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 4, 0, 0, TimeSpan.Zero), DemoResetService.NextRun(now, new TimeOnly(4, 0)));
        var later = new DateTimeOffset(2026, 9, 13, 18, 45, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 4, 0, 0, TimeSpan.Zero), DemoResetService.NextRun(later, new TimeOnly(4, 0)));
    }

    [Fact]
    public void Seed_set_covers_every_status_and_match_tier()
    {
        var apps = DemoSeeder.BuildApplications("u", new DateTime(2026, 9, 13));

        Assert.Equal(15, apps.Count);
        Assert.All(Enum.GetValues<Models.Enums.ApplicationStatus>(), st => Assert.Contains(apps, a => a.Status == st));

        var tiers = apps.Where(a => a.MatchScore.HasValue).Select(a => Helpers.StatusDisplay.MatchTier(a.MatchScore!.Value)).Distinct().ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, tiers.OrderBy(t => t));
        Assert.Contains(apps, a => a.MatchScore == null);                       // some not yet analysed
        Assert.All(apps.Where(a => a.MatchScore.HasValue), a =>
        {
            Assert.Equal(ResumeMatcherService.RecommendationFor(a.MatchScore!.Value), a.MatchRecommendation);
            Assert.False(string.IsNullOrWhiteSpace(a.MatchSummary));
            Assert.False(string.IsNullOrWhiteSpace(a.MatchingSkillsJson));
        });
        Assert.All(apps, a => { Assert.Equal("u", a.UserId); Assert.False(string.IsNullOrWhiteSpace(a.JobDescription)); });
        Assert.Equal(apps.Count, apps.Select(a => (a.CompanyName, a.RoleTitle)).Distinct().Count());
    }
}
