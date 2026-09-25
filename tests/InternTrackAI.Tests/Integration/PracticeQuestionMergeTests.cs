using System.Net;
using System.Text;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Interview prep after the move off <c>InterviewPrepSession</c>: the same two URLs, the same JSON on
/// the wire, one <see cref="PracticeQuestion"/> row per question underneath.
/// </summary>
/// <remarks>
/// This is the step that touched a shipped feature, so the tests are about it still working rather
/// than about the new capabilities. The wire-shape test matters most: <c>Prep.cshtml</c>'s inline
/// script renders straight from that JSON and was deliberately left untouched, so a change to the
/// response shape would break the page with nothing else failing.
/// </remarks>
public class PracticeQuestionMergeTests
{
    /// <summary>Returns a scripted question set instead of calling OpenAI, and counts calls.</summary>
    private sealed class StubPrep : InterviewPrepService
    {
        private readonly Func<List<GeneratedQuestion>> _script;
        public int Calls;

        public StubPrep(IServiceProvider sp, Func<List<GeneratedQuestion>> script)
            : base(new HttpClient(), sp.GetRequiredService<IConfiguration>(),
                   sp.GetRequiredService<ILogger<InterviewPrepService>>())
            => _script = script;

        public override Task<(bool Success, List<GeneratedQuestion> Questions, string? Error)> GenerateAsync(
            string company, string role, string jobDescription, string resumeText, string skills, string? profileContext = null)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult((true, _script(), (string?)null));
        }
    }

    private sealed class Harness : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public StubPrep Prep { get; private set; } = null!;

        public Harness(Func<List<GeneratedQuestion>> script)
        {
            Factory = Parent.WithWebHostBuilder(b => b.ConfigureServices(services =>
            {
                services.RemoveAll<InterviewPrepService>();
                services.AddTransient<InterviewPrepService>(sp => Prep ??= new StubPrep(sp, script));
            }));
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public async Task<string> UserIdOf(string email)
        {
            using var scope = Factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
        }

        public async Task<List<PracticeQuestion>> QuestionsOf(string userId)
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .PracticeQuestions.AsNoTracking().Where(q => q.UserId == userId).OrderBy(q => q.Id).ToListAsync();
        }

        public async Task<int> SeedApplication(string userId, string company = "Shopify", string role = "Backend Intern")
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication { UserId = userId, CompanyName = company, RoleTitle = role, JobDescription = "Build things." };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            return app.Id;
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static List<GeneratedQuestion> ThreeQuestions() => new()
    {
        new(QuestionCategory.Technical,       "What is a hash map?",                    "Mention average-case lookup."),
        new(QuestionCategory.Behavioral,      "Tell me about a time you missed a deadline", "Use STAR."),
        new(QuestionCategory.CompanySpecific, "Why Shopify?",                            null),
    };

    private static async Task<JsonElement> Generate(HttpClient client, int appId)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, $"/InterviewPrep/Prep?appId={appId}");
        var req = new HttpRequestMessage(HttpMethod.Post, "/InterviewPrep/Generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { appId }), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("RequestVerificationToken", token);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<string> PrepPage(HttpClient client, int appId)
    {
        var res = await client.GetAsync($"/InterviewPrep/Prep?appId={appId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    // ── The page still works ─────────────────────────────────────────────────

    [Fact]
    public async Task The_prep_page_renders_its_empty_state_before_anything_is_generated()
    {
        using var h = new Harness(ThreeQuestions);
        var client = h.Client();
        var appId = await h.SeedApplication(await h.UserIdOf(await Http.RegisterAsync(client)));

        var html = await PrepPage(client, appId);

        Assert.Contains("Generate interview prep", html);
        // "Regenerate" also appears inside the page's inline script, so the empty state is asserted
        // on the hint that only renders once something has been generated.
        Assert.DoesNotContain("Last generated on", html);
    }

    [Fact]
    public async Task Generating_stores_one_row_per_question_and_the_page_renders_them()
    {
        using var h = new Harness(ThreeQuestions);
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);

        await Generate(client, appId);

        var stored = await h.QuestionsOf(userId);
        Assert.Equal(3, stored.Count);
        Assert.All(stored, q => Assert.Equal(appId, q.ApplicationId));
        Assert.All(stored, q => Assert.NotEmpty(q.PromptHash));
        Assert.Contains(stored, q => q.Category == QuestionCategory.CompanySpecific);

        // Step 2 fills these; Step 1 leaves the documented defaults.
        Assert.All(stored, q => Assert.Equal(PracticeDifficulty.Medium, q.Difficulty));
        Assert.All(stored, q => Assert.Equal("", q.Topic));

        var html = await PrepPage(client, appId);
        Assert.Contains("What is a hash map?", html);
        Assert.Contains("Mention average-case lookup.", html);
        Assert.Contains("Company-Specific", html);      // the badge still says the old spelling
        Assert.Contains("Last generated on", html);
    }

    [Fact]
    public async Task The_generate_response_is_the_new_questions_rendered_as_practice_cards()
    {
        // Since the prep page's inline script was retired (2026-09-25) there is no client-side card to
        // feed: Generate returns the stored questions rendered by _PrepQuestionGroups, grouped by category,
        // and interview-prep.js merges them into the sections already on the page.
        using var h = new Harness(ThreeQuestions);
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);

        var body = await Generate(client, appId);

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(3, body.GetProperty("added").GetInt32());
        Assert.False(body.TryGetProperty("questions", out _));

        var html = WebUtility.HtmlDecode(body.GetProperty("html").GetString()!);
        foreach (var q in await h.QuestionsOf(userId))
            Assert.Contains($"data-question-id=\"{q.Id}\"", html);
        Assert.Contains("data-prep-category=\"Technical\"", html);
        Assert.Contains("data-prep-category=\"CompanySpecific\"", html);
        Assert.Contains("Company-Specific", html);                 // the badge keeps its display spelling
        Assert.Contains("Mention average-case lookup.", html);     // the tip, now the card's hint
        Assert.Contains("data-practice-answer-form", html);
    }

    // ── What the row-per-question store buys immediately ─────────────────────

    [Fact]
    public async Task Regenerating_does_not_store_the_same_question_twice()
    {
        // The old blob was replaced wholesale, so this was invisible. Now the unique hash index means
        // a model that repeats itself costs nothing.
        using var h = new Harness(ThreeQuestions);
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);

        await Generate(client, appId);
        var second = await Generate(client, appId);

        Assert.Equal(3, (await h.QuestionsOf(userId)).Count);
        // Nothing new to report — and "nothing new" is its own answer, not an empty list the page could
        // mistake for "no questions" and blank itself with, which the old client did.
        Assert.Equal(0, second.GetProperty("added").GetInt32());
        Assert.Equal("", second.GetProperty("html").GetString());
        Assert.Equal(2, h.Prep.Calls);
    }

    [Fact]
    public async Task A_repeat_within_one_batch_is_stored_once()
    {
        using var h = new Harness(() => new()
        {
            new(QuestionCategory.Technical, "What is a hash map?", null),
            new(QuestionCategory.Technical, "what is a HASH MAP",  null),   // same question, different case
        });
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Generate(client, await h.SeedApplication(userId));

        Assert.Single(await h.QuestionsOf(userId));
    }

    [Fact]
    public async Task A_blank_question_is_dropped_rather_than_stored_as_an_empty_card()
    {
        using var h = new Harness(() => new()
        {
            new(QuestionCategory.Technical, "   ", null),
            new(QuestionCategory.Technical, "What is a hash map?", null),
        });
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Generate(client, await h.SeedApplication(userId));

        Assert.Single(await h.QuestionsOf(userId));
    }

    // ── Lifecycle carried over from the sessions it replaces ─────────────────

    [Fact]
    public async Task Deleting_the_application_deletes_its_questions()
    {
        using var h = new Harness(ThreeQuestions);
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);
        await Generate(client, appId);
        Assert.Equal(3, (await h.QuestionsOf(userId)).Count);

        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications");
        var res = await client.PostAsync("/JobApplications/Delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = appId.ToString(), ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        Assert.Empty(await h.QuestionsOf(userId));
    }

    [Fact]
    public async Task Deleting_the_account_deletes_the_questions_with_it()
    {
        using var h = new Harness(ThreeQuestions);
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Generate(client, await h.SeedApplication(userId));

        using (var scope = h.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<UserDataPurger>().PurgeAsync(userId);

        Assert.Empty(await h.QuestionsOf(userId));
    }

    [Fact]
    public async Task One_users_question_does_not_block_another_users_identical_one()
    {
        // The unique index is (UserId, PromptHash), not PromptHash alone — two people can be asked the
        // same interview question, and on a shared demo account that is the normal case.
        using var h = new Harness(ThreeQuestions);

        var alice = h.Client();
        var aliceId = await h.UserIdOf(await Http.RegisterAsync(alice));
        await Generate(alice, await h.SeedApplication(aliceId));

        var bob = h.Client();
        var bobId = await h.UserIdOf(await Http.RegisterAsync(bob));
        await Generate(bob, await h.SeedApplication(bobId));

        Assert.Equal(3, (await h.QuestionsOf(aliceId)).Count);
        Assert.Equal(3, (await h.QuestionsOf(bobId)).Count);
    }
}
