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
/// The three dedupe layers, end to end against a scripted model.
/// </summary>
/// <remarks>
/// Layer 1 (topic steering) is the one that carries the weight, since the hash provably cannot catch
/// paraphrase — so the tests that matter most here are the ones asserting what actually goes into the
/// prompt, not just what comes back out of the database.
/// </remarks>
public class PracticeGenerationTests
{
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
                    services.AddHttpClient<PracticeQuestionService>().ConfigurePrimaryHttpMessageHandler(() => Model));
            });
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

        public async Task<PracticeGenerationResult> Generate(string userId, int count = 5, PracticeDifficulty d = PracticeDifficulty.Medium)
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<PracticeQuestionService>()
                .GenerateAsync(userId, d, QuestionCategory.Technical, count);
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static string Batch(string prefix, int n, int from = 0) =>
        ScriptedOpenAi.Questions(Enumerable.Range(from, n)
            .Select(i => ($"{prefix} question number {i}?", $"{prefix.ToLowerInvariant()} topic {i}")).ToArray());

    // ── Layer 1: topic steering ──────────────────────────────────────────────

    [Fact]
    public async Task The_first_prompt_carries_no_exclusion_block()
    {
        // A first-time user's prompt should not be padded with headings that say "none".
        using var h = new Harness(new ScriptedOpenAi(Batch("Alpha", 7)));
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId);

        Assert.DoesNotContain("already covered", h.Model.Prompts[0]);
        Assert.DoesNotContain("RECENT QUESTION OPENINGS", h.Model.Prompts[0]);
    }

    [Fact]
    public async Task The_second_prompt_excludes_the_topics_from_the_first()
    {
        using var h = new Harness(new ScriptedOpenAi(Batch("Alpha", 7), Batch("Beta", 7)));
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId);
        await h.Generate(userId);

        var second = h.Model.Prompts[1];
        Assert.Contains("already covered", second);
        Assert.Contains("alpha topic 0", second);
        Assert.Contains("alpha topic 4", second);
        Assert.Contains("RECENT QUESTION OPENINGS", second);
        Assert.Contains("Alpha question number", second);

        // The block closes the prompt. Live runs show this changed nothing measurable, but it costs
        // nothing and a later edit that buries it mid-prompt should be a deliberate choice, not a slip.
        Assert.True(second.IndexOf("already covered", StringComparison.Ordinal)
                    > second.IndexOf("Return this JSON object", StringComparison.Ordinal),
            "the exclusion list should be the last thing in the prompt");
    }

    [Fact]
    public async Task Exclusions_are_scoped_to_the_difficulty_and_category_being_asked_for()
    {
        // A topic covered at Easy is still worth a Hard question, so the lists must not bleed.
        using var h = new Harness(new ScriptedOpenAi(Batch("Easy", 7), Batch("Hard", 7)));
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId, d: PracticeDifficulty.Easy);
        await h.Generate(userId, d: PracticeDifficulty.Hard);

        Assert.DoesNotContain("easy topic 0", h.Model.Prompts[1]);
    }

    [Fact]
    public async Task The_prompt_states_the_difficulty_definition_not_just_its_name()
    {
        // Without the definitions a model treats difficulty as a tone adjective and the three tiers
        // come back near-identical.
        using var h = new Harness(new ScriptedOpenAi(Batch("Alpha", 7)));
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId, d: PracticeDifficulty.Hard);

        Assert.Contains("synthesis, edge cases", h.Model.Prompts[0]);
        Assert.DoesNotContain("recall and definitions", h.Model.Prompts[0]);
    }

    // ── Layer 2: hash dedupe ─────────────────────────────────────────────────

    [Fact]
    public async Task A_model_that_repeats_itself_stores_each_question_once()
    {
        // Exactly five per reply, so nothing is left over from over-requesting and the only thing
        // that can change the count is dedupe.
        using var h = new Harness(new ScriptedOpenAi(Batch("Alpha", 5), Batch("Alpha", 5)));
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId);
        var second = await h.Generate(userId);

        Assert.Equal(5, (await h.QuestionsOf(userId)).Count);   // the first batch only
        Assert.Empty(second.Questions);
        Assert.Contains("covered a lot of ground", second.Note);
    }

    [Fact]
    public async Task A_reworded_repeat_is_caught_when_it_is_only_reordered()
    {
        using var h = new Harness(new ScriptedOpenAi(
            ScriptedOpenAi.Questions(("Explain the difference between a mutex and a semaphore?", "locking primitives")),
            ScriptedOpenAi.Questions(("Explain the difference between a semaphore and a mutex?", "locking primitives"))));
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId, count: 1);
        await h.Generate(userId, count: 1);

        Assert.Single(await h.QuestionsOf(userId));
    }

    // ── Layer 3: the top-up, capped ──────────────────────────────────────────

    [Fact]
    public async Task A_short_batch_triggers_exactly_one_top_up()
    {
        // First reply is all duplicates, second supplies fresh ones.
        using var h = new Harness(new ScriptedOpenAi(Batch("Alpha", 5), Batch("Alpha", 5), Batch("Beta", 5)));
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId);                 // call 1: stores 5
        var result = await h.Generate(userId);    // call 2 all duplicates -> call 3 tops up

        Assert.Equal(3, h.Model.Calls);
        Assert.Equal(5, result.Questions.Count);
        Assert.Equal(10, (await h.QuestionsOf(userId)).Count);
    }

    [Fact]
    public async Task The_top_up_never_fires_more_than_once_however_many_duplicates_come_back()
    {
        // The open-wallet guard: an unbounded retry here spends the owner's key in a loop.
        using var h = new Harness(new ScriptedOpenAi(Batch("Alpha", 5)));   // every reply is the same batch
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId);
        var result = await h.Generate(userId);

        Assert.Equal(3, h.Model.Calls);          // 1 initial + 1 duplicate round + 1 top-up, then stop
        Assert.Empty(result.Questions);
        Assert.NotNull(result.Note);             // a note, not an error
        Assert.True(result.Success);
    }

    [Fact]
    public async Task The_top_up_names_a_duplicate_topic_the_base_exclusion_list_did_not_have()
    {
        // The case droppedTopics exists for. A topic already stored is already in the exclusion list,
        // so re-adding it buys nothing; what helps is when the model returns an *already-stored
        // question* under a brand-new topic label. Then the label is news, and the top-up should say so.
        using var h = new Harness(new ScriptedOpenAi(
            ScriptedOpenAi.Questions(("Explain the difference between a mutex and a semaphore?", "locking primitives")),
            ScriptedOpenAi.Questions(("Explain the difference between a semaphore and a mutex?", "mislabelled new topic")),
            ScriptedOpenAi.Questions(("Something genuinely different?", "fresh topic"))));
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId, count: 1);
        await h.Generate(userId, count: 1);

        Assert.Equal(3, h.Model.Calls);
        Assert.DoesNotContain("mislabelled new topic", h.Model.Prompts[1]);   // not known before the round
        Assert.Contains("mislabelled new topic", h.Model.Prompts[2]);         // the top-up learned it
    }

    [Fact]
    public async Task Generation_over_requests_so_a_normal_dedupe_loss_needs_no_retry()
    {
        using var h = new Harness(new ScriptedOpenAi(Batch("Alpha", 7)));
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId, count: 5);

        Assert.Equal(1, h.Model.Calls);
        Assert.Contains($"Write {5 + PracticeQuestionService.OverRequest} interview practice questions", h.Model.Prompts[0]);
    }

    // ── Failures ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_model_error_is_reported_rather_than_stored()
    {
        using var h = new Harness(new ScriptedOpenAi(Batch("Alpha", 7)) { Status = HttpStatusCode.InternalServerError });
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        var result = await h.Generate(userId);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(await h.QuestionsOf(userId));
    }

    [Fact]
    public async Task A_question_with_no_topic_is_still_stored()
    {
        // It just contributes nothing to the next exclusion list — losing the question would be worse.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Questions(("A perfectly good question?", ""))));
        var userId = await h.UserIdOf(await Http.RegisterAsync(h.Client()));

        await h.Generate(userId, count: 1);

        Assert.Single(await h.QuestionsOf(userId));
    }
}
