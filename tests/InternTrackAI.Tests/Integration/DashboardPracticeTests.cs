using System.Net;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The interview-practice card on the dashboard. The arithmetic is <see cref="PracticeProgress"/>'s and
/// is pinned in <c>PracticeProgressTests</c>; this pins that the dashboard shows it, hides it at zero, and
/// cannot disagree with the practice page about the same rows.
/// </summary>
public class DashboardPracticeTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public DashboardPracticeTests(TestAppFactory factory) => _factory = factory;

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<string> RegisterAndGetId(HttpClient client)
    {
        var email = await Http.RegisterAsync(client);
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
    }

    private async Task Seed(string userId, string prompt, int? score = null, DateTime? answeredAt = null, string topic = "a topic")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.PracticeQuestions.Add(new PracticeQuestion
        {
            UserId = userId, Prompt = prompt, Topic = topic, Difficulty = PracticeDifficulty.Medium,
            Category = QuestionCategory.Technical, PromptHash = QuestionHash.Of(prompt), CreatedAt = DateTime.UtcNow,
            Score = score,
            UserAnswer = score is null ? null : "An answer long enough to have been graded properly.",
            AnsweredAt = score is null ? null : answeredAt ?? DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string> Get(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    private static string Stat(string html, string name) =>
        Regex.Replace(Regex.Match(html, $"data-practice-stat=\"{name}\">(.*?)</span>\\s*<span class=\"mini-stat-label\"",
            RegexOptions.Singleline).Groups[1].Value, "<[^>]+>", "").Trim();

    [Fact]
    public async Task The_card_is_absent_until_the_user_has_a_question()
    {
        var client = Client();
        await RegisterAndGetId(client);

        Assert.DoesNotContain("practiceStatsCard", await Get(client, "/Home/Dashboard"));
    }

    [Fact]
    public async Task The_dashboard_and_the_practice_page_report_the_same_numbers_for_the_same_rows()
    {
        var client = Client();
        var userId = await RegisterAndGetId(client);

        await Seed(userId, "Answered well?", score: 4);
        await Seed(userId, "Answered badly?", score: 2);
        await Seed(userId, "Answered in the middle?", score: 3);
        await Seed(userId, "Not answered yet?");

        var dashboard = await Get(client, "/Home/Dashboard");
        var practice  = await Get(client, "/Practice/Progress");

        Assert.Equal("3/4", Stat(dashboard, "answered"));
        Assert.Equal("3/5", Stat(dashboard, "average"));

        // Same figures on the practice page's own card.
        Assert.Contains("3<span class=\"practice-progress-of\">/4</span>", practice);
        Assert.Contains("3<span class=\"practice-progress-of\">/5</span>", practice);
    }

    [Fact]
    public async Task Questions_but_no_answers_show_the_count_and_no_average()
    {
        var client = Client();
        var userId = await RegisterAndGetId(client);
        await Seed(userId, "Waiting for an answer?");

        var html = await Get(client, "/Home/Dashboard");

        Assert.Contains("practiceStatsCard", html);
        Assert.Equal("0/1", Stat(html, "answered"));
        Assert.DoesNotContain("data-practice-stat=\"average\"", html);
        Assert.Contains("None answered yet", html);
    }

    [Fact]
    public async Task The_weakest_topic_floor_holds_on_the_dashboard_too()
    {
        var client = Client();
        var userId = await RegisterAndGetId(client);

        // Two answers on one topic is under MinAnswersForWeakestTopic: no finding.
        await Seed(userId, "First on caching?", score: 1, topic: "cache invalidation");
        await Seed(userId, "Second on caching?", score: 2, topic: "cache invalidation");
        Assert.DoesNotContain("Weakest topic", await Get(client, "/Home/Dashboard"));

        await Seed(userId, "Third on caching?", score: 2, topic: "cache invalidation");
        Assert.Contains("Weakest topic so far: cache invalidation", Regex.Replace(await Get(client, "/Home/Dashboard"), @"<[^>]+>|\s+", m => m.Value.StartsWith('<') ? "" : " "));
    }

    [Fact]
    public async Task The_last_seven_days_are_counted_in_the_users_own_zone()
    {
        var client = Client();
        var userId = await RegisterAndGetId(client);

        // Registered users get the default zone; seed through it (CLAUDE.md §8, time in tests).
        var clock = TestClock.User;
        var windowStart = clock.Today.AddDays(-(PracticeProgress.RecentActivityDays - 1));

        await Seed(userId, "Answered this morning?", score: 4, answeredAt: TestClock.Instant(clock.Today, 9));
        await Seed(userId, "Answered at the very start of the window?", score: 4, answeredAt: TestClock.Instant(windowStart, 0));
        await Seed(userId, "Answered the evening before the window?", score: 4, answeredAt: TestClock.Instant(windowStart.AddDays(-1), 23));

        Assert.Equal("2", Stat(await Get(client, "/Home/Dashboard"), "recent"));
    }

    // ── Connecting applications to practice ─────────────────────────────────

    private async Task<int> SeedApplication(string userId, string company, ApplicationStatus status, DateTime? interviewAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var app = new JobApplication
        {
            UserId = userId, CompanyName = company, RoleTitle = "Backend Intern", Status = status, InterviewAt = interviewAt
        };
        db.JobApplications.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    [Fact]
    public async Task An_interview_on_the_attention_card_links_to_that_applications_interview_prep()
    {
        var client = Client();
        var userId = await RegisterAndGetId(client);
        var appId = await SeedApplication(userId, "Shopify", ApplicationStatus.Interview, TestClock.Instant(TestClock.Today.AddDays(3), 14));

        var html = await Get(client, "/Home/Dashboard");

        var item = Regex.Match(html, $"<li class=\"attention-item[^\"]*\" data-app-id=\"{appId}\" data-kind=\"Interview\"[^>]*>(.*?)</li>", RegexOptions.Singleline);
        Assert.True(item.Success, "the interview should be on the attention card");
        Assert.Contains($"href=\"/InterviewPrep/Prep?appId={appId}\"", item.Groups[1].Value);
        Assert.Contains("Practice for this interview", item.Groups[1].Value);

        // Already on the attention card, so not repeated on the practice card.
        Assert.DoesNotContain("practiceInterviewPrompts", html);
    }

    [Fact]
    public async Task An_interview_with_no_date_is_offered_on_the_practice_card_even_before_any_practice()
    {
        var client = Client();
        var userId = await RegisterAndGetId(client);
        var undated = await SeedApplication(userId, "CIBC", ApplicationStatus.Interview, interviewAt: null);
        await SeedApplication(userId, "Applied Only Inc", ApplicationStatus.Applied, interviewAt: null);

        var html = await Get(client, "/Home/Dashboard");

        // The card appears for the prompt alone; its stats stay hidden at zero questions.
        Assert.Contains("practiceStatsCard", html);
        Assert.DoesNotContain("data-practice-stat=", html);

        var prompts = Regex.Match(html, "id=\"practiceInterviewPrompts\">(.*?)</ul>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("CIBC", prompts);
        Assert.Contains($"/InterviewPrep/Prep?appId={undated}", prompts);
        Assert.DoesNotContain("Applied Only Inc", prompts);
    }

    [Fact]
    public async Task With_no_questions_and_no_interview_the_card_stays_hidden()
    {
        var client = Client();
        var userId = await RegisterAndGetId(client);
        await SeedApplication(userId, "Applied Only Inc", ApplicationStatus.Applied, interviewAt: null);

        Assert.DoesNotContain("practiceStatsCard", await Get(client, "/Home/Dashboard"));
    }
}
