using System.Net;
using System.Text;
using System.Text.Json;
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
/// Interview-prep questions carry a topic, and that topic is <b>descriptive only</b>: it feeds the
/// progress card, and it never takes part in topic dedupe in either direction.
/// </summary>
/// <remarks>
/// <para>
/// The first version of this change let prep topics and practice topics block each other. It passed
/// its tests and then failed the live check: prep topics came back category-level ("Collaboration",
/// "Automated testing") across three calls and two postings, and <see cref="TopicKey"/> treats a short
/// topic as colliding with every longer one containing its words. See
/// <see cref="Models.Enums.QuestionSource"/> for the measurement.
/// </para>
/// <para>
/// So these tests pin the opposite of what an earlier draft pinned. If a future change makes prep topics
/// narrow and wants them in dedupe, these are the tests to change — after re-measuring, not before.
/// </para>
/// </remarks>
public class PrepQuestionTopicTests
{
    private sealed class Harness : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public ScriptedOpenAi PrepModel { get; }
        public ScriptedOpenAi PracticeModel { get; }

        public Harness(string prepReply, string practiceReply)
        {
            PrepModel = new ScriptedOpenAi(prepReply);
            PracticeModel = new ScriptedOpenAi(practiceReply);
            Factory = Parent.WithWebHostBuilder(b =>
            {
                b.UseSetting("OpenAI:ApiKey", "sk-test-not-a-real-key");
                b.ConfigureServices(services =>
                {
                    services.AddHttpClient<InterviewPrepService>().ConfigurePrimaryHttpMessageHandler(() => PrepModel);
                    services.AddHttpClient<PracticeQuestionService>().ConfigurePrimaryHttpMessageHandler(() => PracticeModel);
                });
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public async Task<string> Register(HttpClient client)
        {
            var email = await Http.RegisterAsync(client);
            using var scope = Factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
        }

        public async Task<int> SeedApplication(string userId, string company = "Shopify", string role = "Backend Intern")
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication { UserId = userId, CompanyName = company, RoleTitle = role, JobDescription = "Build and run services." };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            return app.Id;
        }

        public async Task<List<PracticeQuestion>> Stored(string userId)
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .PracticeQuestions.AsNoTracking().Where(q => q.UserId == userId).OrderBy(q => q.Id).ToListAsync();
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    /// <summary>The prep service's reply: a JSON array, as its prompt asks for.</summary>
    private static string PrepReply(params (string Category, string Topic, string Question)[] questions) =>
        JsonSerializer.Serialize(questions.Select(q => new { category = q.Category, topic = q.Topic, question = q.Question, tip = "A tip." }));

    private static async Task<JsonElement> PrepGenerate(HttpClient client, int appId)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, $"/InterviewPrep/Prep?appId={appId}");
        var req = new HttpRequestMessage(HttpMethod.Post, "/InterviewPrep/Generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { appId }), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("RequestVerificationToken", token);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("success").GetBoolean(), body.ToString());
        return body;
    }

    private static async Task PracticeGenerate(HttpClient client, string difficulty, string category = "Technical")
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Practice");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Practice/GenerateMore")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token, ["difficulty"] = difficulty, ["category"] = category
            })
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("success").GetBoolean());
    }

    private const string PrepPooling  = "How would you size the database connection pool for a checkout service at peak?";
    private const string PracticePooling = "Your service exhausts its connection pool under load. How do you diagnose it?";

    private async Task SeedLegacyRow(string userId, string prompt, string topic)
    {
        using var scope = h_Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.PracticeQuestions.Add(new PracticeQuestion
        {
            UserId = userId, Prompt = prompt, Topic = topic, Difficulty = PracticeDifficulty.Medium,
            Category = QuestionCategory.Technical, PromptHash = QuestionHash.Of(prompt),
            CreatedAt = DateTime.UtcNow, Source = null
        });
        await db.SaveChangesAsync();
    }

    private WebApplicationFactory<Program>? h_Factory;

    // ── What a prep row is ───────────────────────────────────────────────────

    [Fact]
    public async Task A_prep_question_stores_its_topic_marked_as_interview_prep_at_Medium()
    {
        using var h = new Harness(
            PrepReply(("Technical", "connection pooling", PrepPooling)),
            ScriptedOpenAi.Questions(("unused", "unused")));
        var client = h.Client();
        var userId = await h.Register(client);

        await PrepGenerate(client, await h.SeedApplication(userId));

        var row = Assert.Single(await h.Stored(userId));
        Assert.Equal("connection pooling", row.Topic);
        Assert.Equal(QuestionSource.InterviewPrep, row.Source);
        Assert.Equal(PracticeDifficulty.Medium, row.Difficulty);
    }

    [Fact]
    public async Task A_practice_question_is_marked_as_practice()
    {
        using var h = new Harness("unused", ScriptedOpenAi.Questions((PracticePooling, "connection pooling")));
        var client = h.Client();
        var userId = await h.Register(client);

        await PracticeGenerate(client, "Medium");

        Assert.All(await h.Stored(userId), q => Assert.Equal(QuestionSource.Practice, q.Source));
    }

    // ── Descriptive only: no topic blocking in either direction ──────────────

    [Fact]
    public async Task A_prep_topic_does_not_block_or_steer_a_Medium_practice_question_on_the_same_subject()
    {
        using var h = new Harness(
            PrepReply(("Technical", "connection pooling", PrepPooling)),
            ScriptedOpenAi.Questions((PracticePooling, "Connection pooling strategies")));
        var client = h.Client();
        var userId = await h.Register(client);

        await PrepGenerate(client, await h.SeedApplication(userId));
        await PracticeGenerate(client, "Medium");

        Assert.Contains(await h.Stored(userId), q => q.Prompt == PracticePooling && q.Source == QuestionSource.Practice);

        // Not in the practice prompt's covered list either: a broad prep topic there would tell the model
        // to stay away from a whole area. (Checked by the block's heading, not the topic string —
        // TopicRule itself uses "connection pooling" as its example of a well-sized topic.)
        Assert.DoesNotContain("BEFORE YOU ANSWER", h.PracticeModel.Prompts[0]);
    }

    [Fact]
    public async Task A_practice_topic_does_not_block_a_prep_question_on_the_same_subject()
    {
        using var h = new Harness(
            PrepReply(("Technical", "automated testing", "Describe your experience with automated testing.")),
            ScriptedOpenAi.Questions(("How would you test a payment flow end to end without charging a card?", "automated testing for payment flows")));
        var client = h.Client();
        var userId = await h.Register(client);

        await PracticeGenerate(client, "Medium");
        var body = await PrepGenerate(client, await h.SeedApplication(userId));

        // The narrow practice topic contains every word of the broad prep one — exactly the shape
        // TopicKey would call a collision. It must not be one here.
        Assert.Single(body.GetProperty("questions").EnumerateArray());
        Assert.Contains(await h.Stored(userId), q => q.Topic == "automated testing" && q.Source == QuestionSource.InterviewPrep);
    }

    [Fact]
    public async Task The_same_question_text_still_dedupes_across_both_pages_by_hash()
    {
        using var h = new Harness(
            PrepReply(("Technical", "connection pooling", PrepPooling)),
            ScriptedOpenAi.Questions((PrepPooling, "a completely different topic label")));
        var client = h.Client();
        var userId = await h.Register(client);

        await PrepGenerate(client, await h.SeedApplication(userId));
        await PracticeGenerate(client, "Medium");

        Assert.Single(await h.Stored(userId), q => q.Prompt == PrepPooling);
    }

    [Fact]
    public async Task Rows_from_before_the_column_still_count_as_practice_topics()
    {
        using var h = new Harness("unused", ScriptedOpenAi.Questions(
            (PracticePooling, "Connection pooling strategies"),
            ("How would you make a payment retry safe to run twice?", "idempotent payment retries")));
        h_Factory = h.Factory;
        var client = h.Client();
        var userId = await h.Register(client);

        // Source = null: stored before the column existed. Null must not be read as "prep", or every
        // existing practice topic silently stops deduplicating the day this deploys.
        await SeedLegacyRow(userId, "What does a connection pool protect you from?", "connection pooling");
        await PracticeGenerate(client, "Medium");

        var stored = await h.Stored(userId);
        Assert.DoesNotContain(stored, q => q.Prompt == PracticePooling);
        Assert.Contains(stored, q => q.Topic == "idempotent payment retries");
    }

    // ── What the topic is for ────────────────────────────────────────────────

    [Fact]
    public async Task Prep_topics_feed_the_progress_cards_weakest_topic()
    {
        using var h = new Harness("unused", "unused");
        var client = h.Client();
        var userId = await h.Register(client);

        using (var scope = h.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var (prompt, score) in new[] { ("Tell me about a team project?", 1), ("Describe working with a difficult teammate?", 2), ("How do you share credit on a team?", 2) })
            {
                db.PracticeQuestions.Add(new PracticeQuestion
                {
                    UserId = userId, Prompt = prompt, Topic = "Team collaboration", Category = QuestionCategory.Behavioral,
                    Difficulty = PracticeDifficulty.Medium, PromptHash = QuestionHash.Of(prompt), Source = QuestionSource.InterviewPrep,
                    CreatedAt = DateTime.UtcNow, Score = score, UserAnswer = "An answer long enough to have been graded.", AnsweredAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }

        var res = await client.GetAsync("/Practice/Progress");
        var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());

        Assert.Contains("<strong>Team collaboration</strong>", html);
    }

    // ── The prompt ───────────────────────────────────────────────────────────

    [Fact]
    public void The_prep_prompt_asks_for_a_topic_using_the_practice_generators_rule_verbatim()
    {
        var prompt = InterviewPrepService.BuildUserPrompt("Shopify", "Backend Intern", "Build services.", "", "");

        // One definition of "topic" across both generators, even though prep's are not used for dedupe.
        Assert.Contains(PracticePrompt.TopicRule, prompt);
        Assert.Contains("\"question\": \"...\", \"topic\": \"...\"", prompt);

        // What was there before is still there.
        Assert.Contains("Include 3–4 Technical, 3–4 Behavioral, 2 Company-Specific questions", prompt);
        Assert.Contains("Never default to software questions for a non-software role.", prompt);
    }
}
