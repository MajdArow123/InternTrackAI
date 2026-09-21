using System.Net;
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
/// Answering a practice question: what is stored, what the card comes back saying, and the two limits
/// that exist to stop a model call being wasted or a column growing without bound.
/// </summary>
public class PracticeAnswerTests
{
    /// <summary>Long enough to clear <see cref="PracticeAnswerService.MinAnswerChars"/>.</summary>
    private const string GoodAnswer =
        "I assessed airway and circulation first, then escalated to the attending once the second arrival destabilised.";

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
                // Stubbed at the transport, so the prompt, the parsing and the rotation are all under
                // test rather than mocked away.
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

        public async Task<PracticeQuestion> Seed(
            string userId,
            string prompt = "How would you triage two arrivals at once?",
            QuestionCategory category = QuestionCategory.Technical,
            int? applicationId = null)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var q = new PracticeQuestion
            {
                UserId        = userId,
                Prompt        = prompt,
                Topic         = "mass-casualty triage",
                Category      = category,
                Difficulty    = PracticeDifficulty.Medium,
                ModelHint     = "Name the protocol · Give the outcome",
                PromptHash    = QuestionHash.Of(prompt),
                ApplicationId = applicationId,
                CreatedAt     = DateTime.UtcNow
            };
            db.PracticeQuestions.Add(q);
            await db.SaveChangesAsync();
            return q;
        }

        public async Task<int> SeedApplication(string userId)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication { UserId = userId, CompanyName = "Sunnybrook", RoleTitle = "Student Nurse", JobDescription = "Ward work." };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            return app.Id;
        }

        public async Task<PracticeQuestion> Reload(int id)
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .PracticeQuestions.AsNoTracking().FirstAsync(q => q.Id == id);
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static async Task<HttpResponseMessage> Submit(HttpClient client, int questionId, string answer, int? elapsedSeconds = null)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Practice");
        var fields = new Dictionary<string, string>
        {
            ["questionId"] = questionId.ToString(),
            ["answer"] = answer,
            ["__RequestVerificationToken"] = token
        };
        if (elapsedSeconds is { } e) fields["elapsedSeconds"] = e.ToString();

        var req = new HttpRequestMessage(HttpMethod.Post, "/Practice/SubmitAnswer")
        {
            Content = new FormUrlEncodedContent(fields)
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(req);
    }

    private static async Task<System.Text.Json.JsonElement> SubmitOk(HttpClient client, int questionId, string answer, int? elapsedSeconds = null)
    {
        var res = await Submit(client, questionId, answer, elapsedSeconds);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    // ── The happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task Submitting_stores_the_answer_the_score_and_when_it_was_answered()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 4)));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var question = await h.Seed(userId);

        var body = await SubmitOk(client, question.Id, GoodAnswer);

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(4, body.GetProperty("score").GetInt32());

        var stored = await h.Reload(question.Id);
        Assert.Equal(GoodAnswer, stored.UserAnswer);
        Assert.Equal(4, stored.Score);
        Assert.NotNull(stored.AnsweredAt);
        Assert.Null(stored.PriorAttemptsJson);      // nothing superseded yet

        // The score lives in its own column as well as inside the JSON, because Phase 5 aggregates it.
        Assert.Contains("\"score\"", stored.AiFeedback);
    }

    [Fact]
    public async Task The_returned_card_is_the_answered_state_the_page_would_have_rendered()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(
            score: 2,
            strengths: new[] { "You started with the airway." },
            improvements: new[] { "Say what the outcome was.", "Name the triage protocol." },
            missingPoints: new[] { "How you escalated." })));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        var html = WebUtility.HtmlDecode((await SubmitOk(client, question.Id, GoodAnswer)).GetProperty("html").GetString());

        Assert.Contains("practice-score--low", html);          // 2 of 5
        Assert.Contains("You started with the airway.", html);
        Assert.Contains("Two things to change", html);
        Assert.Contains("Name the triage protocol.", html);
        Assert.Contains("What you left out", html);
        Assert.Contains("Retry this question", html);
        Assert.Contains(GoodAnswer, html);
    }

    [Fact]
    public async Task Submitting_returns_the_refreshed_progress_card_with_it()
    {
        // The bug this pins was found in production: SubmitAnswer swapped only the question card, so
        // the progress card kept showing the pre-answer figures until a reload. Stale numbers in a card
        // that reads as authoritative are worse than no card.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 4)));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var question = await h.Seed(userId);
        await h.Seed(userId, "A second question nobody has answered?");

        // Before: nothing answered.
        var before = WebUtility.HtmlDecode(await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync());
        Assert.Contains("0<span class=\"practice-progress-of\">/2</span>", before.Replace("&quot;", "\""));

        var body = await SubmitOk(client, question.Id, GoodAnswer);

        var progress = WebUtility.HtmlDecode(body.GetProperty("progress").GetString()!);
        Assert.Contains("id=\"practiceProgress\"", progress);
        Assert.Contains("Your progress", progress);
        Assert.Contains("1<span class=\"practice-progress-of\">/2</span>", progress.Replace("&quot;", "\""));
        Assert.Contains("Average score", progress);
    }

    [Theory]
    [InlineData(1, "practice-score--low")]
    [InlineData(3, "practice-score--mid")]
    [InlineData(5, "practice-score--high")]
    public async Task The_score_chip_colour_follows_the_score(int score, string expectedClass)
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: score)));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        var html = (await SubmitOk(client, question.Id, GoodAnswer)).GetProperty("html").GetString()!;

        Assert.Contains(expectedClass, html);
    }

    [Fact]
    public async Task A_question_generated_for_an_application_tells_the_grader_which_posting()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var question = await h.Seed(userId, applicationId: await h.SeedApplication(userId));

        await SubmitOk(client, question.Id, GoodAnswer);

        Assert.Contains("practising for Student Nurse at Sunnybrook", h.Model.Prompts[0]);
    }

    [Fact]
    public async Task The_grader_is_told_what_the_generator_intended_a_strong_answer_to_cover()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        await SubmitOk(client, question.Id, GoodAnswer);

        Assert.Contains("<strong_answer_covers>", h.Model.Prompts[0]);
        Assert.Contains("Name the protocol", h.Model.Prompts[0]);
    }

    // ── Answer timing (§6) ───────────────────────────────────────────────────

    [Fact]
    public async Task A_reported_answer_time_is_stored_and_shown()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 4)));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        var body = await SubmitOk(client, question.Id, GoodAnswer, elapsedSeconds: 134);

        Assert.Equal(134, (await h.Reload(question.Id)).AnsweredInSeconds);
        Assert.Contains("2m 14s", WebUtility.HtmlDecode(body.GetProperty("html").GetString()));
    }

    [Theory]
    [InlineData(null, null)]      // not reported
    [InlineData(0, null)]         // nonsense
    [InlineData(-5, null)]        // hostile
    [InlineData(99999, 3600)]     // a tab left open, clamped
    [InlineData(45, 45)]
    public async Task A_client_reported_time_is_bounded_rather_than_trusted(int? reported, int? expected)
    {
        // The browser measures this, so it is advisory. Nothing depends on it being truthful.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 3)));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        await SubmitOk(client, question.Id, GoodAnswer, reported);

        Assert.Equal(expected, (await h.Reload(question.Id)).AnsweredInSeconds);
    }

    [Fact]
    public void The_clamp_is_pure_and_pinned()
    {
        Assert.Null(PracticeAnswerService.ElapsedSeconds(null));
        Assert.Null(PracticeAnswerService.ElapsedSeconds(0));
        Assert.Null(PracticeAnswerService.ElapsedSeconds(int.MinValue));
        Assert.Equal(1, PracticeAnswerService.ElapsedSeconds(1));
        Assert.Equal(PracticeAnswerService.MaxAnsweredSeconds, PracticeAnswerService.ElapsedSeconds(int.MaxValue));
    }

    // ── Collapse defaults (§5b) ──────────────────────────────────────────────

    [Fact]
    public async Task A_freshly_scored_card_comes_back_expanded_but_reloads_collapsed()
    {
        // The rule: the card you just finished is the one worth reading, so it opens. Everything the
        // page renders afterwards is history and stays folded, which is what keeps five answered
        // questions scannable.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 4)));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        var justScored = (await SubmitOk(client, question.Id, GoodAnswer)).GetProperty("html").GetString()!;
        Assert.Contains("data-practice-feedback open", justScored);

        var reloaded = await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync();
        Assert.Contains("data-practice-feedback", reloaded);
        Assert.DoesNotContain("data-practice-feedback open", reloaded);
    }

    [Fact]
    public async Task An_answered_card_is_marked_so_the_page_can_be_scanned()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 2)));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var question = await h.Seed(userId);
        await h.Seed(userId, "Still unanswered?");

        await SubmitOk(client, question.Id, GoodAnswer);
        var html = await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync();

        Assert.Contains("practice-card--scored-low", html);      // 2 of 5
        Assert.Contains("data-answered=\"true\"", html);
        Assert.Contains("data-answered=\"false\"", html);
    }

    // ── The batch path (§2) ──────────────────────────────────────────────────

    [Fact]
    public async Task The_progress_endpoint_renders_the_card_on_its_own()
    {
        // The batch button calls this once after every answer has settled. Swapping the progress card
        // per answer instead would make the numbers jitter as calls land out of order.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 4)));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var question = await h.Seed(userId);
        await h.Seed(userId, "A second question?");

        await SubmitOk(client, question.Id, GoodAnswer);

        var res = await client.GetAsync("/Practice/Progress");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
        Assert.Contains("id=\"practiceProgress\"", html);
        Assert.Contains("Your progress", html);
        Assert.Contains("1<span class=\"practice-progress-of\">/2</span>", html);
        Assert.DoesNotContain("practice-card", html);   // the card alone, not the list
    }

    [Fact]
    public async Task One_rate_limited_answer_does_not_stop_the_others_from_being_stored()
    {
        // Partial failure is the normal case for a batch: the calls that fit are scored and the ones
        // that do not say why, rather than the whole batch failing as a unit.
        var parent = new TestAppFactory();
        using var _ = parent;
        var model = new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 4));
        using var factory = parent.WithWebHostBuilder(b =>
        {
            b.UseSetting("OpenAI:ApiKey", "sk-test-not-a-real-key");
            b.UseSetting("RateLimiting:AI:Practice:PermitLimit", "2");
            b.UseSetting("RateLimiting:AI:Practice:WindowMinutes", "60");
            b.ConfigureServices(services =>
                services.AddHttpClient<AnswerFeedbackService>().ConfigurePrimaryHttpMessageHandler(() => model));
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = await Http.RegisterAsync(client);

        string userId;
        using (var scope = factory.Services.CreateScope())
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;

        var ids = new List<int>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var i = 0; i < 3; i++)
            {
                var prompt = $"Batch question {i}?";
                var q = new PracticeQuestion
                {
                    UserId = userId, Prompt = prompt, Topic = "triage", Category = QuestionCategory.Technical,
                    Difficulty = PracticeDifficulty.Medium, PromptHash = QuestionHash.Of(prompt), CreatedAt = DateTime.UtcNow
                };
                db.PracticeQuestions.Add(q);
                await db.SaveChangesAsync();
                ids.Add(q.Id);
            }
        }

        var statuses = new List<HttpStatusCode>();
        foreach (var id in ids)
        {
            var tok = await Http.GetAntiforgeryTokenAsync(client, "/Practice");
            var req = new HttpRequestMessage(HttpMethod.Post, "/Practice/SubmitAnswer")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["questionId"] = id.ToString(), ["answer"] = GoodAnswer, ["__RequestVerificationToken"] = tok
                })
            };
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            statuses.Add((await client.SendAsync(req)).StatusCode);
        }

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests }, statuses);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.PracticeQuestions.AsNoTracking().Where(q => q.UserId == userId).OrderBy(q => q.Id).ToListAsync();

            // The two that fit are scored and durable; the third is untouched, not half-written.
            Assert.Equal(2, stored.Count(q => q.AnsweredAt is not null));
            Assert.Null(stored[^1].AnsweredAt);
            Assert.Null(stored[^1].UserAnswer);
        }
    }

    // ── Retry and the capped history ─────────────────────────────────────────

    [Fact]
    public async Task A_second_attempt_pushes_the_first_into_the_history()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 2), ScriptedOpenAi.Feedback(score: 4)));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        await SubmitOk(client, question.Id, GoodAnswer);
        var second = "This time I named the protocol and gave the outcome, which was both patients stabilised.";
        var html = WebUtility.HtmlDecode((await SubmitOk(client, question.Id, second)).GetProperty("html").GetString());

        var stored = await h.Reload(question.Id);
        Assert.Equal(second, stored.UserAnswer);
        Assert.Equal(4, stored.Score);

        var history = AttemptHistory.Read(stored.PriorAttemptsJson);
        Assert.Equal(GoodAnswer, Assert.Single(history).Answer);
        Assert.Equal(2, history[0].Score);

        Assert.Contains("Earlier attempt (1)", html);
        Assert.Contains(GoodAnswer, html);
    }

    [Fact]
    public async Task A_fifth_attempt_leaves_exactly_three_in_the_history()
    {
        // Same reasoning as pruning parsed resume drafts: an attempt from six tries ago is not something
        // anyone scrolls back to, and an uncapped list in a column is an uncapped column.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        for (var i = 1; i <= 5; i++)
            await SubmitOk(client, question.Id, $"Attempt number {i}: I assessed the airway and then escalated.");

        var stored = await h.Reload(question.Id);
        var history = AttemptHistory.Read(stored.PriorAttemptsJson);

        Assert.Equal("Attempt number 5", stored.UserAnswer![..16]);
        Assert.Equal(AttemptHistory.Keep, history.Count);
        Assert.Equal("Attempt number 4", history[0].Answer[..16]);
        Assert.Equal("Attempt number 2", history[^1].Answer[..16]);
        Assert.DoesNotContain("Attempt number 1", stored.PriorAttemptsJson);
    }

    // ── The two limits ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_too_short_answer_is_rejected_without_a_model_call()
    {
        // The client disables its button as a courtesy; this is the check that stops three words costing
        // a model call, and the only one a scripted request sees.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        var body = await SubmitOk(client, question.Id, "Not much to say.");

        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains($"{PracticeAnswerService.MinAnswerChars} characters", body.GetProperty("error").GetString());
        Assert.Equal(0, h.Model.Calls);
        Assert.Null((await h.Reload(question.Id)).AnsweredAt);
    }

    [Fact]
    public async Task An_empty_answer_is_rejected_without_a_model_call()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        var body = await SubmitOk(client, question.Id, "   ");

        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(0, h.Model.Calls);
    }

    [Fact]
    public async Task A_failed_model_call_leaves_the_previous_attempt_where_it_was()
    {
        // A user who retries into a quota error should not also lose the answer they had.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(score: 4)));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        await SubmitOk(client, question.Id, GoodAnswer);

        h.Model.Status = HttpStatusCode.TooManyRequests;
        var body = await SubmitOk(client, question.Id, "A second attempt that will never get scored at all.");

        Assert.False(body.GetProperty("success").GetBoolean());

        var stored = await h.Reload(question.Id);
        Assert.Equal(GoodAnswer, stored.UserAnswer);
        Assert.Equal(4, stored.Score);
        Assert.Null(stored.PriorAttemptsJson);
    }

    [Fact]
    public async Task Another_users_question_is_not_answerable()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));

        var bob = h.Client();
        var bobsQuestion = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(bob)));

        var alice = h.Client();
        await Http.RegisterAsync(alice);

        var res = await Submit(alice, bobsQuestion.Id, GoodAnswer);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(0, h.Model.Calls);
        Assert.Null((await h.Reload(bobsQuestion.Id)).AnsweredAt);
    }

    // ── The card before it is answered ───────────────────────────────────────

    [Fact]
    public async Task A_behavioural_question_offers_the_STAR_scaffold_and_a_technical_one_does_not()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await h.Seed(userId, "Tell me about a shift that went wrong.", QuestionCategory.Behavioral);

        var html = await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync();

        Assert.Contains("Structure it with STAR", html);
        Assert.Contains("practice-answer-input", html);

        var technicalOnly = h.Client();
        await h.Seed(await h.UserIdOf(await Http.RegisterAsync(technicalOnly)));
        var technicalHtml = await (await technicalOnly.GetAsync("/Practice")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("Structure it with STAR", technicalHtml);
    }

    [Fact]
    public async Task An_answered_question_still_renders_its_feedback_after_a_reload()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback(
            score: 5, strengths: new[] { "Specific and complete." })));
        var client = h.Client();
        var question = await h.Seed(await h.UserIdOf(await Http.RegisterAsync(client)));

        await SubmitOk(client, question.Id, GoodAnswer);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync());

        Assert.Contains("Specific and complete.", html);
        Assert.Contains("practice-score--high", html);
        Assert.Contains("Retry this question", html);
    }

    [Fact]
    public async Task A_row_whose_stored_feedback_is_unreadable_renders_rather_than_breaking_the_page()
    {
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Feedback()));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var question = await h.Seed(userId);

        using (var scope = h.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.PracticeQuestions.FirstAsync(q => q.Id == question.Id);
            row.UserAnswer = GoodAnswer;
            row.AiFeedback = "not json at all";
            row.Score = 3;
            row.AnsweredAt = DateTime.UtcNow;
            row.PriorAttemptsJson = "{{{ broken";
            await db.SaveChangesAsync();
        }

        var res = await client.GetAsync("/Practice");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
        Assert.Contains(GoodAnswer, html);
        Assert.Contains("practice-score--mid", html);
        Assert.DoesNotContain("Earlier attempt", html);
    }
}
