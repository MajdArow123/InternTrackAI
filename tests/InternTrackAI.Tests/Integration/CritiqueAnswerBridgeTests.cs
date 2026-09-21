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
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// <c>/InterviewPrep/CritiqueAnswer</c> after it moved onto <see cref="PracticeAnswerService"/>: the
/// same URL and the same JSON on the wire, one stored attempt underneath.
/// </summary>
/// <remarks>
/// <para>
/// The wire-shape test is the one that matters. <c>Prep.cshtml</c>'s inline script renders straight from
/// <c>{ success, feedback }</c> and was deliberately left untouched — moving it to <c>wwwroot/js</c> is
/// its own change — so a change to the response shape would break that page with nothing else failing.
/// </para>
/// <para>
/// The other half is the hash lookup. The prep page renders from <see cref="PracticeQuestion"/> rows, so
/// hashing the submitted question text finds the row it came from without the client sending an id, which
/// is what lets answers typed there start being stored with no change to the script at all.
/// </para>
/// </remarks>
public class CritiqueAnswerBridgeTests
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

    private static async Task<JsonElement> Critique(HttpClient client, int appId, string question, string answer)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, $"/InterviewPrep/Prep?appId={appId}");
        var req = new HttpRequestMessage(HttpMethod.Post, "/InterviewPrep/CritiqueAnswer")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { appId, question, answer }), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("RequestVerificationToken", token);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    // ── The contract the page still reads ────────────────────────────────────

    [Fact]
    public async Task The_response_is_still_success_and_a_plain_text_feedback_string()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(
            score: 4,
            strengths: new[] { "You gave a measurable result." },
            improvements: new[] { "Say who else was involved.", "Name the deadline." })));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);
        var question = await h.SeedQuestion(userId, appId, "Tell me about a time you fixed something badly designed.");

        var body = await Critique(client, appId, question.Prompt, GoodAnswer);

        Assert.True(body.GetProperty("success").GetBoolean());
        var feedback = body.GetProperty("feedback").GetString()!;

        Assert.Equal(JsonValueKind.String, body.GetProperty("feedback").ValueKind);
        Assert.Contains("Score: 4/5", feedback);
        Assert.Contains("You gave a measurable result.", feedback);
        Assert.Contains("Name the deadline.", feedback);
        Assert.DoesNotContain("{", feedback);      // text, not the JSON the practice page gets
    }

    // ── What it now stores ───────────────────────────────────────────────────

    [Fact]
    public async Task The_attempt_is_stored_against_the_row_the_question_came_from()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 3)));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);
        var question = await h.SeedQuestion(userId, appId, "Tell me about a time you missed a deadline.");

        await Critique(client, appId, question.Prompt, GoodAnswer);

        var stored = await h.Reload(question.Id);
        Assert.Equal(GoodAnswer, stored.UserAnswer);
        Assert.Equal(3, stored.Score);
        Assert.NotNull(stored.AnsweredAt);
    }

    [Fact]
    public async Task An_answer_typed_on_the_prep_page_shows_up_on_the_practice_page()
    {
        // The two pages share one store, which is the whole reason the question systems were merged.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 5)));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);
        var question = await h.SeedQuestion(userId, appId, "Tell me about a time you disagreed with a senior colleague.");

        await Critique(client, appId, question.Prompt, GoodAnswer);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync());

        Assert.Contains(GoodAnswer, html);
        Assert.Contains("practice-score--high", html);
    }

    [Fact]
    public async Task Retrying_on_the_prep_page_rotates_the_earlier_attempt_the_same_way()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 2), ScriptedOpenAi.Feedback(score: 4)));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);
        var question = await h.SeedQuestion(userId, appId, "Tell me about a time you had to learn something fast.");

        await Critique(client, appId, question.Prompt, GoodAnswer);
        await Critique(client, appId, question.Prompt, "A second, longer answer that names the outcome and the people involved.");

        var history = AttemptHistory.Read((await h.Reload(question.Id)).PriorAttemptsJson);
        Assert.Equal(GoodAnswer, Assert.Single(history).Answer);
        Assert.Equal(2, history[0].Score);
    }

    // ── The cases that must not fail ─────────────────────────────────────────

    [Fact]
    public async Task A_question_that_matches_no_stored_row_is_still_scored()
    {
        // A prep page left open across a regenerate. The user gets coached; there is just nowhere to put it.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 3)));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);

        var body = await Critique(client, appId, "A question that was never stored for this user.", GoodAnswer);

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Contains("Score: 3/5", body.GetProperty("feedback").GetString());

        using var scope = h.Factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .PracticeQuestions.CountAsync(q => q.UserId == userId));
    }

    [Fact]
    public async Task Another_users_identical_question_is_not_the_row_that_gets_written()
    {
        // The lookup is (UserId, PromptHash), not PromptHash alone — on the shared demo account two people
        // being asked the same question is the normal case, not an edge one.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        const string shared = "Tell me about a time you worked under pressure.";

        var bob = h.Client();
        var bobId = await h.UserIdOf(await Http.RegisterAsync(bob));
        var bobsQuestion = await h.SeedQuestion(bobId, await h.SeedApplication(bobId), shared);

        var alice = h.Client();
        var aliceId = await h.UserIdOf(await Http.RegisterAsync(alice));
        var aliceApp = await h.SeedApplication(aliceId);
        var alicesQuestion = await h.SeedQuestion(aliceId, aliceApp, shared);

        await Critique(alice, aliceApp, shared, GoodAnswer);

        Assert.Equal(GoodAnswer, (await h.Reload(alicesQuestion.Id)).UserAnswer);
        Assert.Null((await h.Reload(bobsQuestion.Id)).UserAnswer);
    }

    [Fact]
    public async Task A_too_short_answer_is_rejected_without_a_model_call()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);

        var body = await Critique(client, appId, "A question.", "Too short.");

        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains($"{PracticeAnswerService.MinAnswerChars} characters", body.GetProperty("error").GetString());
        Assert.Equal(0, h.Model.Calls);
    }

    [Fact]
    public async Task An_empty_answer_still_says_type_an_answer_first()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var appId = await h.SeedApplication(await h.UserIdOf(await Http.RegisterAsync(client)));

        var body = await Critique(client, appId, "A question.", "   ");

        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("Type an answer first.", body.GetProperty("error").GetString());
        Assert.Equal(0, h.Model.Calls);
    }
}
