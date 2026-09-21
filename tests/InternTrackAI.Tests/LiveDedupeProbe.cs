using System.Text;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace InternTrackAI.Tests;

/// <summary>
/// A one-off diagnostic that generates many questions on one topic area <b>against the real OpenAI
/// API</b> and prints every batch, so a human can judge whether paraphrases are getting past the
/// dedupe layers.
/// </summary>
/// <remarks>
/// <para>
/// <b>This spends money and is off by default.</b> It runs only when <c>INTERNTRACK_LIVE_AI</c> is
/// set, and it refuses to make more than <see cref="MaxCalls"/> requests whatever happens — an
/// unbounded diagnostic around a paid API is the same open-wallet problem the top-up cap exists for.
/// CLAUDE.md §8's working rules require the call budget to be agreed with the maintainer first.
/// </para>
/// <para>
/// It exists because the automated tests cannot answer the question it answers. They prove the
/// mechanism works against a scripted model; only a real model can show whether the topic exclusion
/// list actually <em>steers</em> it, which is the thing carrying the weight now that the hash is known
/// not to catch paraphrase.
/// </para>
/// </remarks>
public class LiveDedupeProbe
{
    public const string EnvVar = "INTERNTRACK_LIVE_AI";

    /// <summary>Hard ceiling, agreed with the maintainer before the run. The handler throws past it.</summary>
    public const int MaxCalls = 12;

    private const int Batches = 6;
    private const int PerBatch = 5;

    private readonly ITestOutputHelper _out;
    public LiveDedupeProbe(ITestOutputHelper output) => _out = output;

    /// <summary>Counts and records every call, and makes exceeding the budget impossible rather than unlikely.</summary>
    private sealed class BudgetedRecorder : DelegatingHandler
    {
        public List<string> Responses { get; } = new();
        public int Calls => Responses.Count;

        public BudgetedRecorder() : base(new HttpClientHandler()) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Calls >= MaxCalls)
                throw new InvalidOperationException($"Call budget of {MaxCalls} exhausted — refusing to spend more.");

            var response = await base.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            string content = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            }
            catch { content = $"<unparseable: {body[..Math.Min(200, body.Length)]}>"; }

            Responses.Add(content);
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        }
    }

    [Fact]
    public async Task Generate_many_questions_on_one_topic_and_print_every_batch()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
        {
            _out.WriteLine($"Skipped: {EnvVar} is not set. This probe calls the real OpenAI API and costs money.");
            return;
        }

        // The real key, read straight from user-secrets and never printed.
        var config = new ConfigurationBuilder()
            .AddUserSecrets("aspnet-InternTrackAI-a9273f32-3acf-454b-ae9a-5c9465b893ec")
            .AddEnvironmentVariables()
            .Build();

        Assert.False(string.IsNullOrWhiteSpace(config["OpenAI:ApiKey"]), "No OpenAI:ApiKey in user-secrets.");

        // A throwaway database, never app.db.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using (var setup = new ApplicationDbContext(options)) await setup.Database.MigrateAsync();

        var recorder = new BudgetedRecorder();
        var http = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(60) };

        const string userId = "live-probe-user";
        const string profileContext =
            "USER PROFILE CONTEXT\nField: Software Engineering (Technology)\nSeniority: Student, ~1 year experience\n" +
            "Location: Toronto, ON\nSkills: Python, SQL, PostgreSQL, REST APIs";

        var callsBefore = new List<int>();
        var storedPerBatch = new List<List<Models.PracticeQuestion>>();
        var notes = new List<string?>();
        var rejections = new List<int>();

        for (var batch = 0; batch < Batches; batch++)
        {
            await using var db = new ApplicationDbContext(options);
            var service = new PracticeQuestionService(db, http, config, NullLogger<PracticeQuestionService>.Instance);

            callsBefore.Add(recorder.Calls);

            // One difficulty, one category, one narrow area — maximum opportunity to repeat.
            var result = await service.GenerateAsync(
                userId, PracticeDifficulty.Medium, QuestionCategory.Technical, PerBatch,
                applicationId: null, profileContext: profileContext);

            if (!result.Success)
            {
                _out.WriteLine($"BATCH {batch + 1} FAILED: {result.Error}");
                break;
            }

            storedPerBatch.Add(result.Questions);
            notes.Add(result.Note);
            rejections.Add(result.TopicRejections);
        }

        // ── Raw output ──
        await using var read = new ApplicationDbContext(options);
        var storedHashes = (await read.PracticeQuestions.AsNoTracking().Select(q => q.PromptHash).ToListAsync())
            .ToHashSet(StringComparer.Ordinal);

        _out.WriteLine($"MODEL CALLS: {recorder.Calls} of {MaxCalls} budget");
        _out.WriteLine("");

        for (var batch = 0; batch < storedPerBatch.Count; batch++)
        {
            var from = callsBefore[batch];
            var to = batch + 1 < callsBefore.Count ? callsBefore[batch + 1] : recorder.Calls;
            var callsThisBatch = to - from;

            _out.WriteLine(new string('=', 78));
            _out.WriteLine($"BATCH {batch + 1}  —  {callsThisBatch} model call{(callsThisBatch == 1 ? "" : "s")}"
                + (callsThisBatch > 1 ? "  ** TOP-UP FIRED **" : "")
                + $"  —  {storedPerBatch[batch].Count} stored"
                + $"  —  {rejections[batch]} TopicKey rejection{(rejections[batch] == 1 ? "" : "s")}");
            if (notes[batch] is { } note) _out.WriteLine($"NOTE: {note}");
            _out.WriteLine(new string('=', 78));

            for (var c = from; c < to; c++)
            {
                if (c >= recorder.Responses.Count) break;
                _out.WriteLine($"-- call {c + 1}{(c > from ? " (top-up)" : "")} --");

                List<GeneratedPracticeQuestion> returned;
                try { returned = PracticeQuestionService.Parse(recorder.Responses[c]); }
                catch (JsonException) { _out.WriteLine("   <malformed JSON>"); continue; }

                foreach (var q in returned)
                {
                    var hash = QuestionHash.Of(q.Prompt);
                    var keptHere = storedPerBatch[batch].Any(s => s.PromptHash == hash);

                    // Which gate stopped it, judged against what was stored *before* this batch.
                    var priorTopics = storedPerBatch.Take(batch).SelectMany(b => b).Select(x => x.Topic).ToList();
                    var status = keptHere            ? "KEPT       "
                               : TopicKey.CollidesWithAny(q.Topic, priorTopics) ? "TOPIC-REJ  "
                               : storedHashes.Contains(hash) ? "HASH-DROP  "
                                                             : "SURPLUS    ";

                    _out.WriteLine($"   [{status}] topic: {q.Topic}");
                    _out.WriteLine($"                 Q: {q.Prompt}");
                }
            }
            _out.WriteLine("");
        }

        _out.WriteLine(new string('=', 78));
        _out.WriteLine("ALL TOPICS IN ORDER STORED:");
        foreach (var q in await read.PracticeQuestions.AsNoTracking().OrderBy(q => q.Id).ToListAsync())
            _out.WriteLine($"   {q.Topic}");
        _out.WriteLine($"TOTAL STORED: {await read.PracticeQuestions.CountAsync()}");
        _out.WriteLine($"TOPIC REJECTIONS PER BATCH: [{string.Join(", ", rejections)}]");
        _out.WriteLine($"CALLS PER BATCH: [{string.Join(", ", Enumerable.Range(0, callsBefore.Count).Select(i => (i + 1 < callsBefore.Count ? callsBefore[i + 1] : recorder.Calls) - callsBefore[i]))}]");
    }
}
