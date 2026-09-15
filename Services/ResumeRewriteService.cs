using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>
/// What the rewrite prompt may use: the chosen application's company, role and stored job description. Nothing else. Profile
/// skills are deliberately left out: in a real-call check the model added a profile skill the bullet never mentioned.
/// </summary>
public sealed record RewriteContext
{
    public int ApplicationId { get; init; }
    public string Company { get; init; } = "";
    public string Role { get; init; } = "";
    public string? JobDescription { get; init; }

    public bool HasJobDescription => !string.IsNullOrWhiteSpace(JobDescription);
}

/// <summary>One rewritten bullet and the short label of the angle it takes ("Impact first", "Technical detail", "Concise").</summary>
public sealed record BulletVariant(string Text, string Angle);

/// <summary>Outcome of a rewrite: 2–3 variants, or a user-facing <see cref="Error"/>. <see cref="Tokens"/> is OpenAI's usage count when reported.</summary>
public sealed record BulletRewriteResult(bool Success, IReadOnlyList<BulletVariant> Variants, string? Error, int? Tokens = null)
{
    public static BulletRewriteResult Ok(IReadOnlyList<BulletVariant> variants, int? tokens = null) => new(true, variants, null, tokens);
    public static BulletRewriteResult Failed(string error) => new(false, Array.Empty<BulletVariant>(), error);
}

/// <summary>
/// "Rewrite a bullet" on the profile's Resume card: one resume bullet plus one application's job description in,
/// two or three rewrites aimed at that posting out. Same call shape as <see cref="ResumeScoreService"/> (raw Chat
/// Completions over HttpClient, gpt-4o-mini, a user-facing error string on every failure) and the same untrusted-input
/// handling as <see cref="FollowUpService"/> (<see cref="PromptData"/>: tagged sections, tag look-alikes stripped,
/// data-only rule). Nothing is persisted and neither the bullet nor the posting is logged. Callers own rate limiting
/// (the "ai" policy) and the demo branch. The endpoint honours <c>OpenAI:BaseUrl</c> so local verification can stub it.
/// </summary>
public class ResumeRewriteService
{
    public const int MaxBulletChars       = 400;
    public const int JobDescriptionBudget = 3000;
    public const int MaxVariantChars      = 300;
    public const int MaxAngleChars        = 40;
    public const int MaxVariants          = 3;
    public const int MinVariants          = 2;

    public const string BadFormatError = "The AI returned the rewrites in an unexpected format. Try again.";
    public const string InventedContentError = "The rewrites added numbers or results that aren't in your bullet, so they were discarded. Try again.";

    /// <summary>The tagged data sections, in prompt order. Any look-alike tag inside untrusted text is removed.</summary>
    public static readonly string[] DataTags = { "application", "job_description", "bullet" };

    /// <summary>Weak openers the prompt names verbatim; a variant never starts with or contains them.</summary>
    public static readonly string[] BannedOpeners = { "Responsible for", "Worked on", "Helped with", "Assisted in", "Involved in" };

    private static readonly Regex TagLookAlike   = PromptData.TagPattern(DataTags);
    private static readonly Regex LeadingGlyph   = new(@"^[\s•\-–—*·▪‣◦●]+", RegexOptions.Compiled);
    private static readonly Regex Placeholder    = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex NumberToken    = new(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled);
    private static readonly Regex WordToken      = new(@"[a-z][a-z0-9+#]*", RegexOptions.Compiled);

    /// <summary>
    /// The vague "-ing outcome clause" verbs the prompt bans unless the bullet states the thing they claim; the clause runs
    /// to the next punctuation. Metric verbs ("reducing", "cutting") are not here: with a placeholder they are the fix.
    /// </summary>
    private static readonly Regex OutcomeClause = new(
        @"\b(?<verb>enhancing|improving|streamlining|optimi[sz]ing|boosting|elevating|strengthening|maximi[sz]ing|focusing\s+on)\b(?<object>[^,.;:!?]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Words that say nothing about what an outcome clause claims, so they neither prove nor disprove it.</summary>
    private static readonly HashSet<string> ClauseFiller = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "for", "to", "in", "on", "with", "by", "from", "across", "its", "their", "into",
        "at", "as", "via", "through", "while", "more", "overall", "better", "greater", "key", "all", "each", "every", "this",
        "that", "these", "those", "both", "other", "new", "existing"
    };

    private readonly HttpClient _http;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ResumeRewriteService> _logger;
    private readonly string _apiKey;
    private readonly string _endpoint;

    public ResumeRewriteService(HttpClient http, ApplicationDbContext db, IConfiguration config, ILogger<ResumeRewriteService> logger)
    {
        _http     = http;
        _db       = db;
        _logger   = logger;
        _apiKey   = config["OpenAI:ApiKey"] ?? string.Empty;
        _endpoint = (config["OpenAI:BaseUrl"]?.TrimEnd('/') ?? "https://api.openai.com") + "/v1/chat/completions";
    }

    // ── Context ──────────────────────────────────────────────────────────────

    /// <summary>Loads the application owner-scoped (null when it isn't <paramref name="userId"/>'s). Nothing from the profile is read.</summary>
    public async Task<RewriteContext?> BuildContextAsync(int applicationId, string userId, CancellationToken ct = default)
    {
        var app = await _db.JobApplications.AsNoTracking()
            .Where(a => a.Id == applicationId && a.UserId == userId)
            .Select(a => new { a.Id, a.CompanyName, a.RoleTitle, a.JobDescription })
            .FirstOrDefaultAsync(ct);
        if (app is null) return null;

        return new RewriteContext
        {
            ApplicationId  = app.Id,
            Company        = app.CompanyName,
            Role           = app.RoleTitle,
            JobDescription = app.JobDescription
        };
    }

    /// <summary>A pasted bullet as one line: leading bullet glyphs or dashes removed, whitespace and line breaks collapsed.</summary>
    public static string NormalizeBullet(string? bullet) => PromptData.OneLine(LeadingGlyph.Replace(bullet ?? "", ""));

    // ── Prompt ───────────────────────────────────────────────────────────────

    private static readonly string Rules = $"""
        Rules for every variant:
        1. One bullet on one line, under 30 words. Recruiters read a bullet in two seconds. No leading bullet character or dash.
        2. Start with a strong past-tense action verb. Never start with, or use anywhere, these phrases: {PromptData.Quoted(BannedOpeners, "; ")}.
        3. Never invent a number. If the original bullet contains a number (a count, percentage, amount, duration or size), carry it through unchanged. Never state a figure that is not in the original bullet as fact, and never calculate a new figure from its numbers.
        4. If the original bullet states no measurable result, the "Impact first" variant must include exactly one bracketed placeholder where the result figure belongs, such as "reducing [metric] by [X]%" or "supporting [N] users", for the applicant to fill in. The other variants may leave it out. If the bullet already states a measurable result, use that result and add no placeholder.
        5. Never add a technology, tool, responsibility, audience or scope that is not in the original bullet. The job description only decides which facts already in the bullet to put first and which of the posting's words to use for them; it never supplies new facts.
        6. Never state an outcome, benefit or improvement that the original bullet does not state. Where a result belongs but the bullet gives none, use the placeholder from rule 4, never a wordy claim. In particular, never add a clause such as "enhancing [thing]", "improving [thing]", "streamlining [thing]", "optimizing [thing]" or "focusing on [thing]" unless the bullet itself states that thing. Bad: "Built [system] with [technology], enhancing functionality and performance". Good: "Built [system] with [technology], reducing [metric] by [X]%".
        7. Keep shared work shared. If the bullet says the work was collaborative ("helped", "assisted", "team", "we", "our", "group" or similar), every variant keeps that in plain words, such as "with a team", "as part of a team" or "as part of a [N]-person team" (a team-size placeholder is separate from the result placeholder in rule 4), and never implies the applicant did it alone. Bad, from "Helped a team build [system] for [project]": "Delivered [system] for [project]". Good: "Built [system] for [project] as part of a team".
        8. Use the posting's vocabulary only where it names the same thing the bullet describes: if the posting says "[term]" and the bullet describes that same work in other words, use "[term]". If no posting term fits, keep the bullet's own words. Never force a keyword in.
        9. Standard resume register: no first person (I, me, my, we, our), no article at the start, no exclamation marks.
        10. The variants take different angles with different sentence structures, not rewordings of one sentence: one leads with the result or impact ("Impact first"), one leads with the technical approach or method ("Technical detail"), and a third, if given, is shorter and blunter, under 15 words ("Concise"). Swapping the verb or moving a phrase is not a new angle: if two variants would share more than about half of the longer one's words, restructure the second (lead with a different fact from the bullet and change what the sentence is about), do not just reword it. If the bullet is too thin for three distinct angles, return two.
        Shape only, not content to reuse: "[Verb]ed [system] with [technology], cutting [metric] by [X]%".
        """;

    private const string OutputRule =
        "Output only a JSON object with one field, \"variants\": an array of 2 or 3 objects, each with string fields " +
        "\"text\" (the bullet) and \"angle\" (a label of at most three words). No markdown, no code fences, no text before or after the JSON.";

    public static readonly string SystemPrompt =
        "You rewrite one resume bullet so it reads stronger for a specific job posting. You return two or three alternative versions of the same bullet.\n\n" +
        PromptData.DataRule(DataTags, "the resume bullet rewrites described here") + "\n\n" + Rules + "\n\n" + OutputRule;

    /// <summary>The user message: the application, the posting and the bullet, each in its tagged section.</summary>
    public static string BuildPrompt(string bullet, RewriteContext c)
    {
        var app = new StringBuilder()
            .Append("Company: ").AppendLine(Clean(PromptData.OneLine(c.Company), 100))
            .Append("Role: ").AppendLine(Clean(PromptData.OneLine(c.Role), 100));

        return "Rewrite the resume bullet for this application.\n\n" +
               string.Join("\n\n",
                   PromptData.Section("application", app.ToString()),
                   PromptData.Section("job_description", string.IsNullOrWhiteSpace(c.JobDescription) ? "(no job description saved)" : Clean(c.JobDescription, JobDescriptionBudget)),
                   PromptData.Section("bullet", Clean(NormalizeBullet(bullet), MaxBulletChars))) +
               "\n\nReturn the JSON object now.";
    }

    private static string Clean(string? text, int budget) => PromptData.Clean(text, budget, TagLookAlike);

    // ── Output parsing ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the model's reply: <c>{"variants":[{text, angle}]}</c>, a bare array of the same objects, or either inside
    /// one markdown fence. Each text is collapsed to one line; blank or over-long texts and repeats are dropped, and so is
    /// any variant that <see cref="InventsNumber"/> or <see cref="InventsOutcome"/> against the original bullet. Keeps at
    /// most three; fewer than two left is an error, as is anything that isn't that shape. Never throws.
    /// </summary>
    public static BulletRewriteResult Parse(string? content, string originalBullet)
    {
        if (string.IsNullOrWhiteSpace(content)) return BulletRewriteResult.Failed(BadFormatError);

        var text = PromptData.UnwrapFence(content);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            JsonElement items;
            if (root.ValueKind == JsonValueKind.Array) items = root;
            else if (root.ValueKind == JsonValueKind.Object && TryProp(root, "variants", out var v) && v.ValueKind == JsonValueKind.Array) items = v;
            else return BulletRewriteResult.Failed(BadFormatError);

            var allowedNumbers = NumberToken.Matches(originalBullet).Select(m => m.Value).ToHashSet();
            var variants = new List<BulletVariant>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var droppedByGuards = 0;

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var variantText = NormalizeBullet(Str(item, "text"));
                if (variantText.Length == 0 || variantText.Length > MaxVariantChars) continue;
                if (!seen.Add(variantText)) continue;
                if (InventsNumber(variantText, allowedNumbers) || InventsOutcome(variantText, originalBullet)) { droppedByGuards++; continue; }

                var angle = PromptData.OneLine(Str(item, "angle"));
                if (angle.Length == 0) angle = "Variant " + (variants.Count + 1);
                if (angle.Length > MaxAngleChars) angle = angle[..MaxAngleChars].TrimEnd();

                variants.Add(new BulletVariant(variantText, angle));
                if (variants.Count == MaxVariants) break;
            }

            if (variants.Count < MinVariants)
                return BulletRewriteResult.Failed(droppedByGuards > 0 ? InventedContentError : BadFormatError);
            return BulletRewriteResult.Ok(variants);
        }
        catch (JsonException)
        {
            return BulletRewriteResult.Failed(BadFormatError);
        }
    }

    /// <summary>True when <paramref name="variant"/> states a number, outside bracketed placeholders, that isn't in the original bullet.</summary>
    public static bool InventsNumber(string variant, IReadOnlySet<string> allowedNumbers) =>
        NumberToken.Matches(Placeholder.Replace(variant, " ")).Any(m => !allowedNumbers.Contains(m.Value));

    /// <summary>
    /// True when <paramref name="variant"/> has a vague outcome clause ("enhancing functionality and performance",
    /// "streamlining internal tools") that claims something the bullet doesn't state. Deliberately lenient so it never
    /// drops a legitimate variant: a clause passes if the bullet uses the same verb ("improved" allows "improving"), if it
    /// holds only placeholders and filler ("improving [metric] by [X]%"), or if <em>any</em> of its words appears in the
    /// bullet ("optimizing the slowest tracking query" restates the bullet). Words match on their first four letters, so
    /// "queries" matches "query". The cost is that a clause mixing a bullet word with an invented one
    /// ("enhancing scheduling app functionality") passes; the prompt carries that case.
    /// </summary>
    public static bool InventsOutcome(string variant, string originalBullet)
    {
        var bulletStems = WordToken.Matches(originalBullet.ToLowerInvariant()).Select(m => Stem(m.Value)).ToHashSet();
        foreach (Match clause in OutcomeClause.Matches(Placeholder.Replace(variant, " ")))
        {
            var verb = clause.Groups["verb"].Value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (bulletStems.Contains(Stem(verb))) continue;

            var words = WordToken.Matches(clause.Groups["object"].Value.ToLowerInvariant())
                .Select(m => m.Value).Where(w => !ClauseFiller.Contains(w)).ToList();
            if (words.Count == 0) continue;
            if (!words.Any(w => bulletStems.Contains(Stem(w)))) return true;
        }
        return false;
    }

    private static string Stem(string word) => word.Length <= 4 ? word : word[..4];

    private static bool TryProp(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string? Str(JsonElement obj, string name) =>
        TryProp(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── Model call ───────────────────────────────────────────────────────────

    /// <summary>Loads the context and rewrites. A missing (or someone else's) application or one without a job description is an error result.</summary>
    public async Task<BulletRewriteResult> RewriteAsync(string bullet, int applicationId, string userId, CancellationToken ct = default)
    {
        var context = await BuildContextAsync(applicationId, userId, ct);
        if (context is null) return BulletRewriteResult.Failed("Application not found.");
        if (!context.HasJobDescription) return BulletRewriteResult.Failed(NoJobDescriptionError);
        return await RewriteAsync(bullet, context, ct);
    }

    public const string NoJobDescriptionError = "This application has no job description yet. Add one to the application first, then rewrite.";

    public async Task<BulletRewriteResult> RewriteAsync(string bullet, RewriteContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return BulletRewriteResult.Failed("OpenAI API key is not configured. Run: dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\"");

        var normalized = NormalizeBullet(bullet);
        var body = new
        {
            model           = "gpt-4o-mini",
            response_format = new { type = "json_object" },
            messages        = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = BuildPrompt(normalized, context) }
            },
            max_tokens  = 400,
            temperature = 0.5
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // Status only: an error body can echo parts of the request.
                _logger.LogWarning("Bullet rewrite failed for application {ApplicationId}: OpenAI returned {Status}.", context.ApplicationId, (int)response.StatusCode);
                return BulletRewriteResult.Failed((int)response.StatusCode switch
                {
                    401 => "Invalid API key. Set it via dotnet user-secrets.",
                    429 => "OpenAI quota exceeded. Add credits at platform.openai.com/settings/billing.",
                    var s => $"OpenAI returned {s}. Try again."
                });
            }

            string? content;
            int? tokens = null;
            using (var doc = JsonDocument.Parse(raw))
            {
                content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                if (doc.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var t))
                    tokens = t;
            }

            var parsed = Parse(content, normalized);
            if (!parsed.Success)
            {
                _logger.LogWarning("Bullet rewrite for application {ApplicationId} came back unusable ({Tokens} tokens).", context.ApplicationId, tokens);
                return parsed;
            }

            _logger.LogInformation("Bullet rewrite for application {ApplicationId}: {Count} variants ({Tokens} tokens).", context.ApplicationId, parsed.Variants.Count, tokens);
            return parsed with { Tokens = tokens };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Bullet rewrite failed for application {ApplicationId} ({Error}).", context.ApplicationId, ex.GetType().Name);
            return BulletRewriteResult.Failed(ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException
                ? BadFormatError
                : "Request to OpenAI failed. Check your network connection.");
        }
    }

    // ── Demo account ─────────────────────────────────────────────────────────

    /// <summary>The sample bullet <see cref="DemoVariants"/> rewrites; the demo UI shows it so nobody mistakes the samples for their own bullet.</summary>
    public const string DemoSampleBullet = "Worked on the backend API for a team class project in ASP.NET Core";

    /// <summary>
    /// The shared demo account's fixed rewrites of <see cref="DemoSampleBullet"/>, no model call. Written to the prompt's
    /// rules: past-tense opener, under 30 words, no new facts or outcomes, the team context kept in every variant, the only
    /// figure a placeholder in the impact-first variant, and no two variants sharing more than half of the longer one's words.
    /// </summary>
    public static IReadOnlyList<BulletVariant> DemoVariants() => new[]
    {
        new BulletVariant("Delivered the ASP.NET Core backend API behind a team class project, exposing [N] endpoints", "Impact first"),
        new BulletVariant("Used ASP.NET Core to build the server-side API layer as one member of a class project team", "Technical detail"),
        new BulletVariant("Built a team project's ASP.NET Core API", "Concise"),
    };
}
