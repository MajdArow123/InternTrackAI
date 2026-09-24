using System.Net;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// How <c>/Practice</c> presents what is already there: the example card on an empty page, and the
/// day separators inside general practice. Presentation only — nothing here is stored or generated.
/// </summary>
public class PracticePresentationTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public PracticePresentationTests(TestAppFactory factory) => _factory = factory;

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<string> UserIdOf(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
    }

    private async Task UpdateProfile(string userId, Action<UserProfile> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.UserProfiles.FirstAsync(p => p.UserId == userId);
        change(profile);
        await db.SaveChangesAsync();
    }

    private async Task SeedQuestion(string userId, string prompt, DateTime createdAtUtc, int? applicationId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.PracticeQuestions.Add(new PracticeQuestion
        {
            UserId = userId, Prompt = prompt, Topic = prompt, Difficulty = PracticeDifficulty.Medium,
            Category = QuestionCategory.Technical, PromptHash = QuestionHash.Of(prompt),
            ApplicationId = applicationId, CreatedAt = createdAtUtc
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string> Page(HttpClient client, string url = "/Practice")
    {
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    private static PracticeExamples ShippedExamples() =>
        new(Path.Combine(AppContext.BaseDirectory, PracticeExamples.RelativePath), NullLogger<PracticeExamples>.Instance);

    // ── The example card on an empty page ────────────────────────────────────

    [Fact]
    public async Task An_empty_page_shows_one_example_from_the_users_own_field()
    {
        var client = Client();
        var userId = await UserIdOf(await Http.RegisterAsync(client));
        await UpdateProfile(userId, p => p.FieldCategory = FieldCategory.Healthcare);

        var html = await Page(client);
        var healthcare = ShippedExamples().For(FieldCategory.Healthcare)!;

        Assert.Contains(healthcare.Question, html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "data-practice-example"));
        Assert.Contains(">Example<", html);

        // Not a question the user has: nothing answerable, nothing starrable, nothing counted.
        Assert.DoesNotContain("data-question-id", html);
        Assert.DoesNotContain("data-practice-star", html);
        Assert.DoesNotContain("id=\"practiceProgress\"", html);
        Assert.DoesNotContain("Manage practice data", html);
    }

    [Fact]
    public async Task A_profile_with_no_field_gets_the_neutral_behavioural_example()
    {
        var client = Client();
        await Http.RegisterAsync(client);

        var html = await Page(client);
        var fallback = ShippedExamples().For(null)!;

        Assert.Equal(QuestionCategory.Behavioral, fallback.Category);
        Assert.Contains(fallback.Question, html);
    }

    [Fact]
    public async Task The_example_disappears_once_the_user_has_any_question_even_when_filtered_to_nothing()
    {
        var client = Client();
        var userId = await UserIdOf(await Http.RegisterAsync(client));
        await SeedQuestion(userId, "A real question of their own?", DateTime.UtcNow);

        Assert.DoesNotContain("data-practice-example", await Page(client));

        // Filtered to nothing is not empty: an example there would read as one of their own questions.
        var filtered = await Page(client, "/Practice?difficulty=Hard");
        Assert.Contains("Nothing at this level yet", filtered);
        Assert.DoesNotContain("data-practice-example", filtered);
    }

    [Fact]
    public async Task A_missing_example_file_leaves_the_empty_page_working_with_no_example()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<PracticeExamples>();
            services.AddSingleton(new PracticeExamples("/nonexistent/practice-examples.json", NullLogger<PracticeExamples>.Instance));
        }));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Http.RegisterAsync(client);

        var html = await Page(client);

        Assert.Contains("No questions yet", html);
        Assert.DoesNotContain("data-practice-example", html);
    }

    [Fact]
    public void The_shipped_file_has_an_example_for_every_field_but_Other_and_a_behavioural_default()
    {
        var (byCategory, fallback) = PracticeExamples.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, PracticeExamples.RelativePath)));

        var missing = Enum.GetValues<FieldCategory>()
            .Where(c => c != FieldCategory.Other && !byCategory.ContainsKey(c))
            .ToList();
        Assert.Empty(missing);

        Assert.NotNull(fallback);
        Assert.Equal(QuestionCategory.Behavioral, fallback!.Category);

        // "Other" has no field of its own, so it gets the question that fits any field.
        var shipped = ShippedExamples();
        Assert.Equal(shipped.For(null)!.Question, shipped.For(FieldCategory.Other)!.Question);

        // The point of keying by field: a nurse is not shown a database question.
        Assert.DoesNotContain("query", byCategory[FieldCategory.Healthcare].Question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_keys_numbers_and_blank_questions_are_skipped_rather_than_breaking_the_parse()
    {
        var (byCategory, fallback) = PracticeExamples.Parse("""
            {
              "_comment": "ignored",
              "5": { "question": "A number is not a field name." },
              "Nursing": { "question": "Not a FieldCategory member." },
              "Finance": { "question": "   " },
              "Legal": { "question": "Kept?", "difficulty": "3", "category": "Nonsense" }
            }
            """);

        Assert.Null(fallback);
        Assert.Single(byCategory);
        Assert.Equal(PracticeDifficulty.Medium, byCategory[FieldCategory.Legal].Difficulty);
        Assert.Equal(QuestionCategory.Technical, byCategory[FieldCategory.Legal].Category);
    }

    // ── Day separators in general practice ───────────────────────────────────

    [Fact]
    public async Task General_practice_is_split_by_the_users_local_day_not_the_UTC_day()
    {
        var client = Client();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        // Tokyo is UTC+9, so 00:30 today and 23:30 yesterday there are an hour apart in UTC and on the
        // same UTC date. A UTC implementation puts them under one label; the user's zone splits them.
        await UpdateProfile(userId, p => p.TimeZoneId = "Asia/Tokyo");
        var tokyo = UserClock.For("Asia/Tokyo");

        await SeedQuestion(userId, "Written just after midnight, their time?", tokyo.ToUtc(tokyo.Today.AddMinutes(30)));
        await SeedQuestion(userId, "Written just before midnight, their time?", tokyo.ToUtc(tokyo.Today.AddDays(-1).AddHours(23.5)));
        await SeedQuestion(userId, "Written three days ago?", tokyo.ToUtc(tokyo.Today.AddDays(-3).AddHours(12)));

        var html = await Page(client);

        var todayAt     = html.IndexOf(">Today<", StringComparison.Ordinal);
        var yesterdayAt = html.IndexOf(">Yesterday<", StringComparison.Ordinal);
        var olderLabel  = tokyo.Today.AddDays(-3).ToString(
            tokyo.Today.AddDays(-3).Year == tokyo.Today.Year ? "dddd, MMM d" : UserClock.DateFormat,
            System.Globalization.CultureInfo.InvariantCulture);
        var olderAt     = html.IndexOf(">" + olderLabel + "<", StringComparison.Ordinal);

        Assert.True(todayAt > 0 && yesterdayAt > todayAt && olderAt > yesterdayAt,
            $"expected Today < Yesterday < {olderLabel}, got {todayAt}, {yesterdayAt}, {olderAt}");

        // Each question sits under its own day.
        var afterMidnight  = html.IndexOf("Written just after midnight", StringComparison.Ordinal);
        var beforeMidnight = html.IndexOf("Written just before midnight", StringComparison.Ordinal);
        Assert.InRange(afterMidnight, todayAt, yesterdayAt);
        Assert.InRange(beforeMidnight, yesterdayAt, olderAt);
    }

    [Fact]
    public async Task A_page_with_nothing_from_today_still_carries_a_hidden_Today_block_for_the_next_batch()
    {
        var client = Client();
        var userId = await UserIdOf(await Http.RegisterAsync(client));
        await SeedQuestion(userId, "Only an old question here?", TestClock.Instant(TestClock.Today.AddDays(-5), 12));

        var html = await Page(client);

        // "Get more" appends into this block. Without it the first batch of a day lands under an
        // older date's label — or practice.js has to build markup the server owns.
        Assert.Matches("<div class=\"practice-day\" data-practice-today hidden>", html);
    }

    [Fact]
    public async Task Application_groups_are_not_split_by_day()
    {
        var client = Client();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        int appId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication { UserId = userId, CompanyName = "Sunnybrook", RoleTitle = "Student Nurse" };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            appId = app.Id;
        }

        await SeedQuestion(userId, "Posting question from today?", DateTime.UtcNow, appId);
        await SeedQuestion(userId, "Posting question from last week?", TestClock.Instant(TestClock.Today.AddDays(-7), 12), appId);

        var html = await Page(client);

        Assert.DoesNotContain("practice-day", html);
    }
}
