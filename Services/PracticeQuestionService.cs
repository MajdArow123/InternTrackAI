using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>What one "get more" produced: the new rows, and a note when it came up short.</summary>
public sealed record PracticeGenerationResult(
    bool Success,
    List<PracticeQuestion> Questions,
    string? Note,
    string? Error)
{
    /// <summary>Questions rejected because their topic was already covered. Diagnostic for <c>LiveDedupeProbe</c>.</summary>
    public int TopicRejections { get; init; }

    public static PracticeGenerationResult Failed(string error) => new(false, new(), null, error);
}

/// <summary>
/// Generates practice questions that don't repeat themselves, in three layers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layer 1 — topic steering, in the prompt. Measured, and it does not work.</b> The topics already
/// covered go into the prompt as an exclusion list, and two instrumented live runs (2026-09-21, 12
/// calls) show the model ignoring it — see CLAUDE.md §8 for the numbers. It is kept because it costs
/// almost nothing and may help at the margin, but <b>nothing here should be assumed to depend on it</b>.
/// What the prompt does reliably deliver is topic <em>granularity</em>, which is what makes layer 2's
/// comparison possible at all.
/// </para>
/// <para>
/// <b>Layer 2 — mechanical rejection on write. This is what actually deduplicates.</b>
/// <see cref="TopicKey"/> rejects a question whose topic is already covered, and
/// <see cref="QuestionHash"/> plus the unique index rejects one whose text is already stored. Of the
/// two, TopicKey does the work: across both live runs the hash fired <b>zero</b> times in 84
/// questions while TopicKey caught 4 in 30. Advisory prompts can be ignored; a comparison cannot.
/// </para>
/// <para>
/// <b>Layer 3 — one top-up retry. Untested in production.</b> If dedupe dropped enough that the user
/// is short, one more call asks for the shortfall with the dropped topics added to the exclusion list.
/// <b>Capped at one</b> — an unbounded retry loop around an AI call, on a public demo paying with the
/// owner's key, is an open wallet. It is covered by tests against a scripted model, but
/// <b>it has never fired against the real API</b>: over-requesting by <see cref="OverRequest"/>
/// covered every shortfall across both live runs. Treat it as unexercised, not as working.
/// </para>
/// <para>
/// Over-requesting by <see cref="OverRequest"/> is the cheap version of the same idea: asking for two
/// extra costs a fraction of a call, and absorbs the usual one-or-two drop without a second round trip.
/// </para>
/// </remarks>
public class PracticeQuestionService
{
    /// <summary>Topics passed into the prompt. Enough to steer, capped so the prompt stays cheap after hundreds of questions.</summary>
    public const int MaxTopicsInPrompt = 60;

    /// <summary>Recent question openings passed in, so framing varies too.</summary>
    public const int MaxStemsInPrompt = 20;

    /// <summary>Extra questions requested beyond what the user asked for, to absorb dedupe losses.</summary>
    public const int OverRequest = 2;

    /// <summary>Hard cap. Not a tuning knob — see the class remarks.</summary>
    public const int MaxTopUps = 1;

    public const int MaxCount = 10;
    private const int MaxHintChars = 400;

    private readonly ApplicationDbContext _db;
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly ILogger<PracticeQuestionService> _logger;

    private static readonly JsonSerializerOptions _camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public PracticeQuestionService(ApplicationDbContext db, HttpClient http, IConfiguration config, ILogger<PracticeQuestionService> logger)
    {
        _db = db;
        _http = http;
        _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
        // Honours OpenAI:BaseUrl so the whole generation loop can be exercised against a stub locally
        // without spending anything — the only way to watch the dedupe layers work over many rounds.
        _endpoint = (config["OpenAI:BaseUrl"]?.TrimEnd('/') ?? "https://api.openai.com") + "/v1/chat/completions";
        _logger = logger;
    }

    /// <summary>
    /// Generates up to <paramref name="count"/> questions the user does not already have, and stores them.
    /// </summary>
    public async Task<PracticeGenerationResult> GenerateAsync(
        string userId,
        PracticeDifficulty difficulty,
        QuestionCategory category,
        int count,
        int? applicationId = null,
        string? profileContext = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return PracticeGenerationResult.Failed("OpenAI API key is not configured.");

        count = Math.Clamp(count, 1, MaxCount);

        var (company, role, jobDescription) = await ApplicationContextAsync(userId, applicationId, ct);
        var exclusions = await ExclusionsAsync(userId, difficulty, category, ct);
        var known = await KnownHashesAsync(userId, ct);

        var kept = new List<PracticeQuestion>();
        var droppedTopics = new List<string>();
        var knownTopics = new List<string>(exclusions.Topics);
        var topicRejections = 0;

        for (var attempt = 0; attempt <= MaxTopUps; attempt++)
        {
            var shortfall = count - kept.Count;
            if (shortfall <= 0) break;

            // The top-up learns from the first round: whatever came back as a duplicate is named as a
            // covered topic, so the model is not steered into the same corner twice.
            var promptExclusions = attempt == 0
                ? exclusions
                : exclusions with { Topics = exclusions.Topics.Concat(droppedTopics).Distinct().Take(MaxTopicsInPrompt).ToList() };

            var prompt = PracticePrompt.Build(
                difficulty, category, shortfall + OverRequest, promptExclusions,
                profileContext, company, role, jobDescription);

            var (ok, generated, error) = await CallModelAsync(prompt, ct);
            if (!ok) return kept.Count == 0 ? PracticeGenerationResult.Failed(error!) : Done(kept, count, topicRejections, difficulty, category);

            foreach (var g in generated)
            {
                if (kept.Count == count) break;

                var hash = QuestionHash.Of(g.Prompt);
                if (hash.Length == 0) continue;

                // Topic first: it catches the reorderings and filler-padded repeats the prompt lets
                // through, which the prompt-text hash cannot see because the wording genuinely differs.
                if (TopicKey.CollidesWithAny(g.Topic, knownTopics))
                {
                    topicRejections++;
                    if (!string.IsNullOrWhiteSpace(g.Topic)) droppedTopics.Add(g.Topic);
                    continue;
                }

                if (!known.Add(hash))
                {
                    if (!string.IsNullOrWhiteSpace(g.Topic)) droppedTopics.Add(g.Topic);
                    continue;
                }

                // Claimed immediately so two questions in one batch can't share a topic either.
                if (!string.IsNullOrWhiteSpace(g.Topic)) knownTopics.Add(g.Topic);

                kept.Add(new PracticeQuestion
                {
                    UserId        = userId,
                    ApplicationId = applicationId,
                    Prompt        = g.Prompt,
                    Topic         = g.Topic,
                    Difficulty    = difficulty,
                    Category      = category,
                    ModelHint     = g.ModelHint,
                    PromptHash    = hash,
                    CreatedAt     = DateTime.UtcNow
                });
            }
        }

        var saved = await SaveAsync(kept, ct);
        return Done(saved, count, topicRejections, difficulty, category);
    }

    /// <summary>
    /// Turns the outcome into what the page says. Coming up short is normal on a well-covered topic and
    /// is reported as a fact, not an error — an error would tell the user something is broken when the
    /// truth is that they have practised this a lot.
    /// </summary>
    private static PracticeGenerationResult Done(
        List<PracticeQuestion> kept, int asked, int topicRejections,
        PracticeDifficulty difficulty, QuestionCategory category)
    {
        // Naming the combination matters more than it looks. The filters can both read "Any" while the
        // generator is producing Medium/Technical, so "try another difficulty or category" against an
        // unchanged count reads as the button having done nothing at all — which is exactly how an
        // exhausted combination got reported as a broken progress card.
        var combination = $"{difficulty} · {QuestionCategories.Display(category)}";

        if (kept.Count == 0)
            return new PracticeGenerationResult(true, kept,
                       $"No new questions this time — you've already covered {combination} pretty thoroughly. Pick a different difficulty or category above and try again.", null)
                   { TopicRejections = topicRejections };

        var note = kept.Count < asked
            ? $"Generated {kept.Count} new {combination} question{(kept.Count == 1 ? "" : "s")} — you've covered a lot of ground here."
            : null;

        return new PracticeGenerationResult(true, kept, note, null) { TopicRejections = topicRejections };
    }

    /// <summary>
    /// Writes the rows. The unique index can still fire on a race between two generations, so a failed
    /// batch degrades to row-by-row and the losers are dropped: fewer questions, never an error.
    /// </summary>
    private async Task<List<PracticeQuestion>> SaveAsync(List<PracticeQuestion> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return rows;

        _db.PracticeQuestions.AddRange(rows);
        try
        {
            await _db.SaveChangesAsync(ct);
            return rows;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            _logger.LogInformation("Practice question batch hit the unique index; retrying row by row.");
        }

        var saved = new List<PracticeQuestion>();
        foreach (var row in rows)
        {
            _db.PracticeQuestions.Add(row);
            try { await _db.SaveChangesAsync(ct); saved.Add(row); }
            catch (DbUpdateException) { _db.ChangeTracker.Clear(); }
        }

        return saved;
    }

    /// <summary>
    /// Layer 1's input: distinct topics already covered at this difficulty and category, plus the
    /// openings of recent questions. Scoped to the pair because a topic covered at Easy is still worth
    /// a Hard question.
    /// </summary>
    public async Task<PracticeExclusions> ExclusionsAsync(string userId, PracticeDifficulty difficulty, QuestionCategory category, CancellationToken ct = default)
    {
        var recent = await _db.PracticeQuestions.AsNoTracking()
            .Where(q => q.UserId == userId && q.Difficulty == difficulty && q.Category == category)
            .OrderByDescending(q => q.Id)
            .Select(q => new { q.Topic, q.Prompt })
            .Take(MaxTopicsInPrompt * 2)      // room for duplicates before the distinct
            .ToListAsync(ct);

        var topics = recent
            .Select(r => r.Topic)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTopicsInPrompt)
            .ToList();

        var stems = recent
            .Take(MaxStemsInPrompt)
            .Select(r => PracticePrompt.Stem(r.Prompt))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PracticeExclusions(topics, stems);
    }

    /// <summary>Every hash this user already has — layer 2's in-memory half, one query.</summary>
    private async Task<HashSet<string>> KnownHashesAsync(string userId, CancellationToken ct)
    {
        var hashes = await _db.PracticeQuestions.AsNoTracking()
            .Where(q => q.UserId == userId)
            .Select(q => q.PromptHash)
            .ToListAsync(ct);

        return new HashSet<string>(hashes, StringComparer.Ordinal);
    }

    /// <summary>Owner-scoped: a foreign application id yields no context rather than leaking one.</summary>
    private async Task<(string? Company, string? Role, string? JobDescription)> ApplicationContextAsync(string userId, int? applicationId, CancellationToken ct)
    {
        if (applicationId is not { } id) return (null, null, null);

        var app = await _db.JobApplications.AsNoTracking()
            .Where(a => a.Id == id && a.UserId == userId)
            .Select(a => new { a.CompanyName, a.RoleTitle, a.JobDescription })
            .FirstOrDefaultAsync(ct);

        return app is null ? (null, null, null) : (app.CompanyName, app.RoleTitle, app.JobDescription);
    }

    private async Task<(bool Ok, List<GeneratedPracticeQuestion> Questions, string? Error)> CallModelAsync(string userPrompt, CancellationToken ct)
    {
        var body = new
        {
            model = "gpt-4o-mini",
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = PracticePrompt.SystemPrompt },
                new { role = "user",   content = userPrompt }
            },
            max_tokens = 1600,
            // Higher than the parsers' 0.1: variety is the product here, and near-zero temperature is
            // itself a cause of repetition.
            temperature = 0.8
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body, _camel), Encoding.UTF8, "application/json");

        try
        {
            var response = await _http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Practice generation failed: OpenAI returned {Status}.", (int)response.StatusCode);
                return (false, new(), (int)response.StatusCode switch
                {
                    401 => "Invalid API key.",
                    429 => "OpenAI quota exceeded. Add credits at platform.openai.com/settings/billing.",
                    _   => $"OpenAI returned {(int)response.StatusCode}."
                });
            }

            using var doc = JsonDocument.Parse(raw);
            var content = PromptData.UnwrapFence(doc.RootElement
                .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}");

            return (true, Parse(content), null);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Practice generation returned malformed JSON.");
            return (false, new(), "The question generator returned an unexpected format. Try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI for practice questions.");
            return (false, new(), "Request to OpenAI failed. Check your connection.");
        }
    }

    /// <summary>
    /// Reads the reply. Never throws on shape: a question missing its topic is still usable (it just
    /// contributes nothing to the next exclusion list), a question missing its prompt is not.
    /// </summary>
    public static List<GeneratedPracticeQuestion> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("questions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();

        var results = new List<GeneratedPracticeQuestion>();

        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;

            var prompt = PromptData.OneLine(Str(e, "prompt"));
            if (prompt.Length == 0) continue;

            results.Add(new GeneratedPracticeQuestion(
                prompt,
                PromptData.OneLine(Str(e, "topic")),
                Hint(e)));
        }

        return results;
    }

    /// <summary>modelHint arrives as bullets; stored as one string because that is how the card shows it.</summary>
    private static string? Hint(JsonElement e)
    {
        if (!e.TryGetProperty("modelHint", out var hint)) return null;

        var text = hint.ValueKind switch
        {
            JsonValueKind.Array => string.Join(" · ", hint.EnumerateArray()
                .Where(h => h.ValueKind == JsonValueKind.String)
                .Select(h => PromptData.OneLine(h.GetString()))
                .Where(h => h.Length > 0)),
            JsonValueKind.String => PromptData.OneLine(hint.GetString()),
            _ => ""
        };

        if (text.Length == 0) return null;
        return text.Length <= MaxHintChars ? text : text[..MaxHintChars].TrimEnd() + " …";
    }

    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>One question off the model, before it becomes a row.</summary>
public sealed record GeneratedPracticeQuestion(string Prompt, string Topic, string? ModelHint);
