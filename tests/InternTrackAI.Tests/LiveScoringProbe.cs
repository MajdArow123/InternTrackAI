using System.Text;
using System.Text.Json;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace InternTrackAI.Tests;

/// <summary>
/// Scores the same two answers against the <b>real API</b> with the old scoring prompt and the new one,
/// so a claim that the tightening worked can be measured rather than asserted.
/// </summary>
/// <remarks>
/// <para>
/// <b>This spends money and is off by default.</b> It runs only when <c>INTERNTRACK_LIVE_AI</c> is set
/// and refuses to exceed <see cref="MaxCalls"/> whatever happens, like <see cref="LiveDedupeProbe"/>.
/// CLAUDE.md §8 requires the budget to be agreed with the maintainer first; this one was agreed at 6.
/// </para>
/// <para>
/// <b>Both answers, both prompts, is the point.</b> A prompt that scores everything lower is not
/// stricter, it is miscalibrated, and only the good answer can tell those apart. The junk answer is the
/// one that came back 3/5 from production with a manufactured strength attached.
/// </para>
/// </remarks>
public class LiveScoringProbe
{
    public const string EnvVar = "INTERNTRACK_LIVE_AI";

    /// <summary>Hard ceiling, agreed before the run: 2 answers × 2 prompts, with headroom for a retry.</summary>
    public const int MaxCalls = 6;

    private readonly ITestOutputHelper _out;
    public LiveScoringProbe(ITestOutputHelper output) => _out = output;

    private const string Question =
        "When would you choose a linear process over a non-linear one, and when would you not?";

    /// <summary>The production answer, verbatim in spirit: four sentences, no specifics, one bare claim.</summary>
    private const string JunkAnswer =
        "Linear processes are more structured and non-linear ones are more flexible. " +
        "Both approaches have their advantages and disadvantages depending on the situation. " +
        "It really comes down to what the team prefers and what the project needs. " +
        "Agile is usually better for most teams these days.";

    private const string GoodAnswer =
        "I'd go linear when the sequence itself is the deliverable — on the medication reconciliation " +
        "audit I ran last year, every step had to be signed off in order because the regulator wanted a " +
        "trail. Non-linear when the work is discovery: on the triage dashboard we reordered the backlog " +
        "twice after the first two user interviews, and finishing the original spec would have shipped " +
        "the wrong thing. The cost of linear there would have been three weeks of build on a screen " +
        "nobody used.";

    /// <summary>Counts and records every call, and makes exceeding the budget impossible rather than unlikely.</summary>
    private sealed class BudgetedHandler : DelegatingHandler
    {
        public int Calls { get; private set; }
        public BudgetedHandler() : base(new HttpClientHandler()) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Calls >= MaxCalls)
                throw new InvalidOperationException($"Call budget of {MaxCalls} exhausted — refusing to spend more.");
            Calls++;
            return base.SendAsync(request, ct);
        }
    }

    /// <summary>
    /// The scoring rule as it stood before the tightening, so "before" is a real comparison rather than
    /// a memory. Kept verbatim; do not edit it to match a later change.
    /// </summary>
    private const string OldScoringRule =
        "SCORE, 1-5. Score what was actually said, not how hard they tried:\n" +
        "- 1 — does not answer the question, or is wrong on the substance.\n" +
        "- 2 — gestures at the right area but stays vague: no specifics, no example, or a serious gap.\n" +
        "- 3 — correct and relevant, but generic. The kind of answer anyone who had read about the topic " +
        "could give. No concrete detail of their own.\n" +
        "- 4 — correct, specific, and grounded in a real example or real detail. An interviewer would be " +
        "satisfied. Something is still missing or unpolished.\n" +
        "- 5 — complete and specific, covers the trade-offs or the outcome, and would stand out against " +
        "other candidates.\n" +
        "Most real answers are a 2 or a 3. Do not give a 4 for an answer with no specifics in it, and do " +
        "not soften the score to be kind — a score that is always 4 tells the candidate nothing.";

    private const string OldStrengthsRule =
        "- strengths: 1-3 short sentences naming what genuinely works. If nothing does, return an empty list rather than inventing praise.\n";

    [Fact]
    public async Task Score_the_same_answers_under_the_old_and_new_prompts()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
        {
            _out.WriteLine($"Skipped: {EnvVar} is not set. This probe calls the real OpenAI API and costs money.");
            return;
        }

        var config = new ConfigurationBuilder()
            .AddUserSecrets("aspnet-InternTrackAI-a9273f32-3acf-454b-ae9a-5c9465b893ec")
            .AddEnvironmentVariables()
            .Build();

        Assert.False(string.IsNullOrWhiteSpace(config["OpenAI:ApiKey"]), "No OpenAI:ApiKey in user-secrets.");

        var handler = new BudgetedHandler();
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var service = new AnswerFeedbackService(http, config, NullLogger<AnswerFeedbackService>.Instance);

        foreach (var (label, answer) in new[] { ("JUNK", JunkAnswer), ("GOOD", GoodAnswer) })
        {
            foreach (var (variant, prompt) in new[]
                     {
                         ("BEFORE", Downgrade(Build(answer))),
                         ("AFTER",  Build(answer)),
                     })
            {
                var feedback = await CallAsync(http, config, prompt);

                _out.WriteLine(new string('=', 78));
                _out.WriteLine($"{label} answer — {variant} prompt");
                _out.WriteLine(new string('=', 78));
                _out.WriteLine($"SCORE: {feedback.Score}/5");
                _out.WriteLine($"STRENGTHS AS RETURNED ({feedback.RawStrengths.Count}):");
                foreach (var s in feedback.RawStrengths) _out.WriteLine($"   - {s}");
                _out.WriteLine($"STRENGTHS AFTER THE EmptyPraise GUARD ({feedback.Filtered.Count}):");
                if (feedback.Filtered.Count == 0) _out.WriteLine("   (none — the card omits \"What worked\")");
                foreach (var s in feedback.Filtered) _out.WriteLine($"   - {s}");
                _out.WriteLine("");
            }
        }

        _out.WriteLine($"MODEL CALLS: {handler.Calls} of {MaxCalls} budget");
        Assert.True(handler.Calls <= MaxCalls);
    }

    /// <summary>
    /// A third variant, run on the 2 calls the first test left in the agreed budget of 6. The prose
    /// tightening moved nothing, so this tries the sharpest mechanical form of the same instruction:
    /// a hard cap tied to something the model can check rather than judge.
    /// </summary>
    [Fact]
    public async Task A_hard_specificity_cap_is_the_last_prompt_variant_worth_trying()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
        {
            _out.WriteLine($"Skipped: {EnvVar} is not set.");
            return;
        }

        var config = new ConfigurationBuilder()
            .AddUserSecrets("aspnet-InternTrackAI-a9273f32-3acf-454b-ae9a-5c9465b893ec")
            .AddEnvironmentVariables()
            .Build();

        var handler = new BudgetedHandler();
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        const string HardCap =
            "\n\nHARD RULE, apply before anything else. Scan the answer for a CONCRETE ANCHOR: a number, " +
            "a date or duration, a named tool/method/policy/product, a named role or person, or a specific " +
            "situation the candidate says actually happened to them. " +
            "If there is NO concrete anchor, the score is AT MOST 2, no matter how fluent, balanced or " +
            "correct the answer is. State which anchor you found, or that you found none, as the first " +
            "item in missingPoints.";

        foreach (var (label, answer) in new[] { ("JUNK", JunkAnswer), ("GOOD", GoodAnswer) })
        {
            var feedback = await CallAsync(http, config, Build(answer) + HardCap);

            _out.WriteLine(new string('=', 78));
            _out.WriteLine($"{label} answer — HARD-CAP prompt");
            _out.WriteLine(new string('=', 78));
            _out.WriteLine($"SCORE: {feedback.Score}/5");
            foreach (var s in feedback.RawStrengths) _out.WriteLine($"   strength: {s}");
            foreach (var m in feedback.Missing) _out.WriteLine($"   missing:  {m}");
            _out.WriteLine("");
        }

        _out.WriteLine($"MODEL CALLS: {handler.Calls} of {MaxCalls} budget");
    }

    private static string Build(string answer) =>
        AnswerFeedbackPrompt.Build(
            new AnswerContext(Question, answer, QuestionCategory.Technical, PracticeDifficulty.Medium),
            "USER PROFILE CONTEXT\nField: Project Management (Business)\nSeniority: Student, ~1 year experience");

    /// <summary>Rewrites the current prompt back to the pre-tightening wording, so both halves differ only in that.</summary>
    private static string Downgrade(string prompt)
    {
        var start = prompt.IndexOf("SCORE, 1-5.", StringComparison.Ordinal);
        var end = prompt.IndexOf("Return this JSON object", StringComparison.Ordinal);
        var downgraded = prompt[..start] + OldScoringRule + "\n\n" + prompt[end..];

        var sStart = downgraded.IndexOf("- strengths:", StringComparison.Ordinal);
        var sEnd = downgraded.IndexOf("- improvements:", StringComparison.Ordinal);
        return downgraded[..sStart] + OldStrengthsRule + downgraded[sEnd..];
    }

    private sealed record Scored(int Score, IReadOnlyList<string> RawStrengths, IReadOnlyList<string> Filtered, IReadOnlyList<string> Missing);

    private static async Task<Scored> CallAsync(HttpClient http, IConfiguration config, string userPrompt)
    {
        var body = new
        {
            model = "gpt-4o-mini",
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = AnswerFeedbackPrompt.SystemPrompt },
                new { role = "user",   content = userPrompt }
            },
            max_tokens = 800,
            temperature = 0.3
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new("Bearer", config["OpenAI:ApiKey"]);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var content = PromptData.UnwrapFence(
            doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}");

        // Read the strengths raw as well as filtered, so the guard's effect is visible separately from
        // the prompt's.
        using var reply = JsonDocument.Parse(content);
        var rawStrengths = reply.RootElement.TryGetProperty("strengths", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : new List<string>();

        var parsed = AnswerFeedback.FromJson(content);
        return new Scored(parsed.Score, rawStrengths, parsed.Strengths, parsed.MissingPoints);
    }
}
