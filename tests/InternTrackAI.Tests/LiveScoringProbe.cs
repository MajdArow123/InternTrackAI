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

    /// <summary>
    /// An answer in the shape the maintainer reported scoring 3/5 in production: specific, technical,
    /// names real tools and a real constraint. <b>Written here, not theirs</b> — their exact text was
    /// described rather than pasted, and answer wording is precisely what is being scored, so this is a
    /// data point about the <em>class</em> of answer and not a reproduction of their result.
    /// </summary>
    private const string SpecificTechnicalAnswer =
        "I'd migrate incrementally rather than all at once. On our checkout service we turned on " +
        "allowJs and converted leaf modules first, leaning on compiler-assisted refactoring so the " +
        "rename of the cart total field propagated instead of being hand-chased. The thing that slowed " +
        "us down was third-party type lag — two of our SDKs shipped no types for months, so we wrote " +
        "local declaration files and deleted them as upstream caught up. I'd accept that cost again " +
        "because the alternative is a big-bang rewrite nobody can review.";

    /// <summary>
    /// Scores today's deployed prompt against the two fixtures whose scores are already recorded, to
    /// answer one question: <b>did ConcreteAnchorRule tighten the bottom of the scale, or all of it?</b>
    /// </summary>
    /// <remarks>
    /// The recorded baseline (2026-09-21, same fixtures): junk 3/5 before the rule and 2/5 after;
    /// specific 4/5 both times. If the specific answer still scores 4 here, the scale did not move and
    /// a 3 on some other specific answer is a judgement about that answer. If it now scores 3, the rule
    /// pulled the whole scale down, which is the "harsher not stricter" failure the before/after was
    /// built to detect. 3 calls.
    /// </remarks>
    [Fact]
    public async Task Score_todays_prompt_against_the_recorded_baseline()
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

        var cases = new[]
        {
            ("JUNK (recorded: 3 before rule, 2 after)", JunkAnswer),
            ("SPECIFIC (recorded: 4 before and after)", GoodAnswer),
            ("SPECIFIC-TECHNICAL (new, reconstruction)", SpecificTechnicalAnswer),
        };

        foreach (var (label, answer) in cases)
        {
            var scored = await CallAsync(http, config, Build(answer));

            _out.WriteLine(new string('=', 78));
            _out.WriteLine($"{label}  ->  {scored.Score}/5");
            _out.WriteLine(new string('=', 78));
            foreach (var s in scored.RawStrengths) _out.WriteLine($"   strength: {s}");
            foreach (var m in scored.Missing)      _out.WriteLine($"   missing:  {m}");
            _out.WriteLine("");
        }

        _out.WriteLine($"MODEL CALLS: {handler.Calls} of {MaxCalls} budget");
    }

    /// <summary>
    /// The two answers the maintainer reported scoring 3/5 in production, verbatim, with their
    /// questions. 2 calls.
    /// </summary>
    /// <remarks>
    /// Prediction recorded before running, so the result can contradict it: both are specific about
    /// <em>mechanisms</em> ("the compiler finds every call site", "Testing Library", "the empty string,
    /// whitespace, and the malformed case") but contain nothing the candidate personally did — no
    /// project, no incident, no outcome. The 3-band says exactly that: "correct and relevant, but
    /// generic. The kind of answer anyone who had read about the topic could give. No concrete detail
    /// of their own."
    /// </remarks>
    [Fact]
    public async Task Score_the_two_answers_reported_as_over_corrected()
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

        const string q1 = "In your opinion, what are the advantages of using TypeScript over JavaScript for a large codebase?";
        const string a1 =
            "The real win is that refactoring stops being guesswork. In a large codebase, renaming a field " +
            "or changing a function signature in plain JS means grepping and hoping; with types the compiler " +
            "finds every call site. Second is that types document intent at the boundary \u2014 I can read a " +
            "function's signature and know what it accepts without reading its body or its tests. The cost is " +
            "real though: build tooling, `any` creeping in under deadline pressure, and third-party types that " +
            "lag the library. I'd take it on anything a team maintains for more than a few months, and skip it " +
            "for a throwaway script.";

        const string q2 = "How would you approach writing unit tests for a function that processes user input in a React application?";
        const string a2 =
            "I'd separate the logic from the component first. If the function processes input, it should be " +
            "testable without rendering anything \u2014 pass in a value, assert on the output, cover the empty " +
            "string, whitespace, and the malformed case. Then test the component separately with Testing " +
            "Library, driving it the way a user would: type into the field, assert what appears on screen, " +
            "rather than reaching into state. The thing I'd avoid is testing implementation details, because " +
            "those tests break on every refactor and pass while the feature is broken, which is the worst of both.";

        const string softwareProfile =
            "USER PROFILE CONTEXT\nField: Software Engineering (Technology)\nSeniority: Student, ~1 year experience";

        foreach (var (label, question, answer) in new[] { ("Q1 TypeScript", q1, a1), ("Q2 React testing", q2, a2) })
        {
            var prompt = AnswerFeedbackPrompt.Build(
                new AnswerContext(question, answer, QuestionCategory.Technical, PracticeDifficulty.Medium),
                softwareProfile);

            var scored = await CallAsync(http, config, prompt);

            _out.WriteLine(new string('=', 78));
            _out.WriteLine($"{label}  ->  {scored.Score}/5   (production reported 3/5)");
            _out.WriteLine(new string('=', 78));
            foreach (var s in scored.RawStrengths) _out.WriteLine($"   strength: {s}");
            foreach (var m in scored.Missing)      _out.WriteLine($"   MISSING:  {m}");
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
