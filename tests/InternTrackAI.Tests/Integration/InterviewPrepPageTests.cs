using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The interview prep page after its inline script was retired (2026-09-25): questions render with the
/// practice page's own card, and answering goes through <c>/Practice/SubmitAnswer</c> by question id.
/// </summary>
/// <remarks>
/// Replaces <c>CritiqueAnswerBridgeTests</c>, which pinned the old <c>{ success, feedback }</c> plain-text
/// contract and the hash lookup that let an id-less script store an answer. Both are gone with
/// <c>CritiqueAnswer</c>. What these pin instead is the gap that bridge left open: an answer typed on this
/// page is now <em>visible</em> on this page, scored, the way it has been on /Practice since Phase 4.
/// </remarks>
public class InterviewPrepPageTests
{
    private const string GoodAnswer =
        "I rebuilt the intake form so the validation ran server-side, which cut the bad submissions to nearly zero.";

    private sealed class Harness : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public ScriptedOpenAi Model { get; }

        public Harness(ScriptedOpenAi model)
        {
            Model = model;
            Factory = Parent.WithWebHostBuilder(b =>
            {
                b.UseSetting("OpenAI:ApiKey", "sk-test-not-a-real-key");
                b.ConfigureServices(services =>
                    services.AddHttpClient<AnswerFeedbackService>().ConfigurePrimaryHttpMessageHandler(() => Model));
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public async Task<string> UserIdOf(string email)
        {
            using var scope = Factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
        }

        public async Task<int> SeedApplication(string userId)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication { UserId = userId, CompanyName = "Shopify", RoleTitle = "Backend Intern", JobDescription = "Build things." };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            return app.Id;
        }

        public async Task<PracticeQuestion> SeedQuestion(string userId, int appId, string prompt)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var q = new PracticeQuestion
            {
                UserId = userId, ApplicationId = appId, Prompt = prompt,
                Category = QuestionCategory.Behavioral, PromptHash = QuestionHash.Of(prompt), CreatedAt = DateTime.UtcNow
            };
            db.PracticeQuestions.Add(q);
            await db.SaveChangesAsync();
            return q;
        }

        public async Task<PracticeQuestion> Reload(int id)
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .PracticeQuestions.AsNoTracking().FirstAsync(q => q.Id == id);
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static async Task<string> PrepPage(HttpClient client, int appId)
    {
        var res = await client.GetAsync($"/InterviewPrep/Prep?appId={appId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    /// <summary>Answers a card the way practice.js does from the prep page: its question id, the page's token.</summary>
    private static async Task<JsonElement> Answer(HttpClient client, int appId, int questionId, string answer)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, $"/InterviewPrep/Prep?appId={appId}");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Practice/SubmitAnswer")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["questionId"] = questionId.ToString(),
                ["answer"] = answer,
            })
        };
        req.Headers.Add("RequestVerificationToken", token);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private static int InlineScripts(string html) =>
        Regex.Matches(html, @"<script(?![^>]*\bsrc=)(?![^>]*application/json)[^>]*>").Count;

    // ── The page itself ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_page_adds_no_inline_script_of_its_own()
    {
        // The layout still carries its own inline blocks (§8), so compare against a page that adds none
        // rather than asserting zero: this pins what the prep page contributes, which was ~170 lines.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var appId = await h.SeedApplication(await h.UserIdOf(await Http.RegisterAsync(client)));

        var prep = await PrepPage(client, appId);
        var practice = WebUtility.HtmlDecode(await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync());

        Assert.Equal(InlineScripts(practice), InlineScripts(prep));
        Assert.Contains("/js/interview-prep.js", prep);
        Assert.Contains("/js/practice.js", prep);
        Assert.DoesNotContain("CritiqueAnswer", prep);
    }

    [Fact]
    public async Task Questions_render_as_practice_cards_grouped_by_category_with_the_token_practice_js_reads()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);
        var question = await h.SeedQuestion(userId, appId, "Tell me about a time you fixed something badly designed.");

        var html = await PrepPage(client, appId);

        Assert.Contains("id=\"practiceList\"", html);
        Assert.Matches("data-practice-token[^>]*>\\s*<input name=\"__RequestVerificationToken\"", html);
        Assert.Contains("data-prep-category=\"Behavioral\"", html);
        Assert.Contains($"data-question-id=\"{question.Id}\"", html);
        Assert.Contains("data-practice-answer-form", html);
    }

    [Fact]
    public async Task The_page_renders_its_empty_list_container_before_any_question_exists()
    {
        // practice.js binds answering to #practiceList once, at load; cards Generate appends later only
        // work because the container was already there.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var appId = await h.SeedApplication(await h.UserIdOf(await Http.RegisterAsync(client)));

        var html = await PrepPage(client, appId);

        Assert.Contains("id=\"practiceList\"", html);
        Assert.DoesNotContain("data-question-id", html);
    }

    // ── Answering from this page ─────────────────────────────────────────────

    [Fact]
    public async Task An_answer_submitted_from_the_prep_page_is_stored_and_the_page_then_shows_it_scored()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 4)));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);
        var question = await h.SeedQuestion(userId, appId, "Tell me about a time you missed a deadline.");

        var body = await Answer(client, appId, question.Id, GoodAnswer);
        Assert.True(body.GetProperty("success").GetBoolean());

        var stored = await h.Reload(question.Id);
        Assert.Equal(GoodAnswer, stored.UserAnswer);
        Assert.Equal(4, stored.Score);

        // The Phase 4 gap: the prep card used to have no answered state at all.
        var html = await PrepPage(client, appId);
        Assert.Contains("practice-score--high", html);
        Assert.Contains(GoodAnswer, html);
    }

    [Fact]
    public async Task An_answer_typed_on_the_prep_page_shows_up_on_the_practice_page()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 5)));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);
        var question = await h.SeedQuestion(userId, appId, "Tell me about a time you disagreed with a senior colleague.");

        await Answer(client, appId, question.Id, GoodAnswer);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync());
        Assert.Contains(GoodAnswer, html);
        Assert.Contains("practice-score--high", html);
    }

    [Fact]
    public async Task The_old_critique_endpoint_is_gone()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        await Http.RegisterAsync(client);

        var res = await client.PostAsync("/InterviewPrep/CritiqueAnswer", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(0, h.Model.Calls);
    }
}
