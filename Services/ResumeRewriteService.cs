using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>What the rewrite prompt may use: the chosen application's company, role and stored job description, plus the profile's skills. Nothing else.</summary>
public sealed record RewriteContext
{
    public int ApplicationId { get; init; }
    public string Company { get; init; } = "";
    public string Role { get; init; } = "";
    public string? JobDescription { get; init; }
    public IReadOnlyList<string> Skills { get; init; } = Array.Empty<string>();

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
    public const int SkillsBudget         = 600;
    public const int MaxVariantChars      = 300;
    public const int MaxAngleChars        = 40;
    public const int MaxVariants          = 3;
    public const int MinVariants          = 2;

    public const string BadFormatError = "The AI returned the rewrites in an unexpected format. Try again.";
    public const string InventedNumberError = "The rewrites added numbers that aren't in your bullet, so they were discarded. Try again.";

    /// <summary>The tagged data sections, in prompt order. Any look-alike tag inside untrusted text is removed.</summary>
    public static readonly string[] DataTags = { "application", "applicant_skills", "job_description", "bullet" };

    /// <summary>Weak openers the prompt names verbatim; a variant never starts with or contains them.</summary>
    public static readonly string[] BannedOpeners = { "Responsible for", "Worked on", "Helped with", "Assisted in", "Involved in" };

    private static readonly Regex TagLookAlike   = PromptData.TagPattern(DataTags);
    private static readonly Regex LeadingGlyph   = new(@"^[\s•\-–—*·▪‣◦●]+", RegexOptions.Compiled);
    private static readonly Regex Placeholder    = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex NumberToken    = new(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled);

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

    /// <summary>Loads the application owner-scoped (null when it isn't <paramref name="userId"/>'s) and the profile's skills.</summary>
    public async Task<RewriteContext?> BuildContextAsync(int applicationId, string userId, CancellationToken ct = default)
    {
        var app = await _db.JobApplications.AsNoTracking()
            .Where(a => a.Id == applicationId && a.UserId == userId)
            .Select(a => new { a.Id, a.CompanyName, a.RoleTitle, a.JobDescription })
            .FirstOrDefaultAsync(ct);
        if (app is null) return null;

        var skillsJson = await _db.UserProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.SkillsJson)
            .FirstOrDefaultAsync(ct);

        return new RewriteContext
        {
            ApplicationId  = app.Id,
            Company        = app.CompanyName,
            Role           = app.RoleTitle,
            JobDescription = app.JobDescription,
            Skills         = ProfileTags.FromJson(skillsJson)
        };
    }

    /// <summary>A pasted bullet as one line: leading bullet glyphs or dashes removed, whitespace and line breaks collapsed.</summary>
    public static string NormalizeBullet(string? bullet) => PromptData.OneLine(LeadingGlyph.Replace(bullet ?? "", ""));

    // ── Prompt ───────────────────────────────────────────────────────────────

    private static readonly string Rules = $"""
        Rules for every variant:
        1. One bullet on one line, under 30 words. Recruiters read a bullet in two seconds. No leading bullet character or dash.
        2. Start with a strong past-tense action verb. Never start with, or use anywhere, these phrases: {PromptData.Quoted(BannedOpeners, "; ")}.
        3. Never invent a number. If the original bullet contains a number (a count, percentage, amount, duration or size), carry it through unchanged. If it contains none, you may mark where one belongs with a bracketed placeholder the applicant fills in, such as "by [X]%" or "for [N] users". Never state a figure that is not in the original bullet as fact, and never calculate a new figure from its numbers.
        4. Never add a technology, tool, responsibility, team, audience or scope that is not in the original bullet. The job description and the applicant's skills only decide which facts already in the bullet to put first and which of the posting's words to use for them; they never supply new facts. A skill in <applicant_skills> that the bullet does not mention stays out.
        5. Use the posting's vocabulary only where it names the same thing the bullet describes: if the posting says "[term]" and the bullet describes that same work in other words, use "[term]". If no posting term fits, keep the bullet's own words. Never force a keyword in.
        6. Standard resume register: no first person (I, me, my, we, our), no article at the start, no exclamation marks.
        7. The variants take different angles, not rewordings of one sentence: one leads with the result or impact ("Impact first"), one leads with the technical approach ("Technical detail"), and a third, if given, is shorter and blunter, under 15 words ("Concise"). If the bullet states no result, the impact-first variant leads with what the work delivered and uses a placeholder for any figure.
        Shape only, not content to reuse: "[Verb]ed [system] with [technology], cutting [metric] by [X]%".
        """;

    private const string OutputRule =
        "Output only a JSON object with one field, \"variants\": an array of 2 or 3 objects, each with string fields " +
        "\"text\" (the bullet) and \"angle\" (a label of at most three words). No markdown, no code fences, no text before or after the JSON.";

    public static readonly string SystemPrompt =
        "You rewrite one resume bullet so it reads stronger for a specific job posting. You return two or three alternative versions of the same bullet.\n\n" +
        PromptData.DataRule(DataTags, "the resume bullet rewrites described here") + "\n\n" + Rules + "\n\n" + OutputRule;

    /// <summary>The user message: the application, the applicant's skills, the posting and the bullet, each in its tagged section.</summary>
    public static string BuildPrompt(string bullet, RewriteContext c)
    {
        var app = new StringBuilder()
            .Append("Company: ").AppendLine(Clean(PromptData.OneLine(c.Company), 100))
            .Append("Role: ").AppendLine(Clean(PromptData.OneLine(c.Role), 100));

        return "Rewrite the resume bullet for this application.\n\n" +
               string.Join("\n\n",
                   PromptData.Section("application", app.ToString()),
                   PromptData.Section("applicant_skills", c.Skills.Count > 0 ? Clean(string.Join(", ", c.Skills), SkillsBudget) : "(none listed)"),
                   PromptData.Section("job_description", string.IsNullOrWhiteSpace(c.JobDescription) ? "(no job description saved)" : Clean(c.JobDescription, JobDescriptionBudget)),
                   PromptData.Section("bullet", Clean(NormalizeBullet(bullet), MaxBulletChars))) +
               "\n\nReturn the JSON object now.";
    }

    private static string Clean(string? text, int budget) => PromptData.Clean(text, budget, TagLookAlike);

    // ── Output parsing ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the model's reply: <c>{"variants":[{text, angle}]}</c>, a bare array of the same objects, or either inside
    /// one markdown fence. Each text is collapsed to one line; blank or over-long texts and repeats are dropped, and so is
    /// any variant stating a number (outside <c>[...]</c> placeholders) that the original bullet doesn't contain. Keeps
    /// at most three; fewer than two left is an error, as is anything that isn't that shape. Never throws.
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
            var droppedForNumbers = 0;

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var variantText = NormalizeBullet(Str(item, "text"));
                if (variantText.Length == 0 || variantText.Length > MaxVariantChars) continue;
                if (!seen.Add(variantText)) continue;
                if (InventsNumber(variantText, allowedNumbers)) { droppedForNumbers++; continue; }

                var angle = PromptData.OneLine(Str(item, "angle"));
                if (angle.Length == 0) angle = "Variant " + (variants.Count + 1);
                if (angle.Length > MaxAngleChars) angle = angle[..MaxAngleChars].TrimEnd();

                variants.Add(new BulletVariant(variantText, angle));
                if (variants.Count == MaxVariants) break;
            }

            if (variants.Count < MinVariants)
                return BulletRewriteResult.Failed(droppedForNumbers > 0 ? InventedNumberError : BadFormatError);
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
    /// rules: past-tense opener, under 30 words, no new facts, the only figure a bracketed placeholder.
    /// </summary>
    public static IReadOnlyList<BulletVariant> DemoVariants() => new[]
    {
        new BulletVariant("Delivered the ASP.NET Core backend API behind a team class project, exposing [N] endpoints", "Impact first"),
        new BulletVariant("Built an ASP.NET Core backend API as the server side of a team class project", "Technical detail"),
        new BulletVariant("Built a team project's ASP.NET Core API", "Concise"),
    };
}
