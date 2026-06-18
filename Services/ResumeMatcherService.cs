using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InternTrackAI.Models.ViewModels;
using UglyToad.PdfPig;

namespace InternTrackAI.Services;

/// <summary>
/// Compares a candidate's resume text against a job description using the OpenAI API and
/// produces a fit score, recommendation tier, and matching/missing skill breakdown. Used by
/// <c>ProfileController.AutoMatch</c> after the AI Job Analyzer extracts a job description, and
/// also extracts text from uploaded resume PDFs via PdfPig.
/// </summary>
public class ResumeMatcherService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<ResumeMatcherService> _logger;

    private static readonly JsonSerializerOptions _camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ResumeMatcherService(HttpClient http, IConfiguration config, ILogger<ResumeMatcherService> logger)
    {
        _http = http;
        _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
        _logger = logger;
    }

    /// <summary>
    /// Extracts plain text from a PDF resume using PdfPig (a managed, dependency-free PDF text
    /// extractor — no external binaries or native libraries required, which keeps the app easy
    /// to deploy on Railway). Words are space-joined per line; layout/formatting is discarded
    /// since only the raw text is needed for the OpenAI prompt.
    /// </summary>
    /// <param name="stream">An open, readable stream positioned at the start of the PDF file.</param>
    /// <returns>The extracted text, trimmed of leading/trailing whitespace.</returns>
    public static string ExtractPdfText(Stream stream)
    {
        var sb = new StringBuilder();
        using var pdf = PdfDocument.Open(stream);
        foreach (var page in pdf.GetPages())
        {
            foreach (var word in page.GetWords())
                sb.Append(word.Text).Append(' ');
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Scores how well a resume matches a job description via OpenAI (GPT-4o-mini).
    /// </summary>
    /// <param name="resumeText">Plain text extracted from the candidate's active resume.</param>
    /// <param name="jobDescription">The job description to match against.</param>
    /// <returns>
    /// A <see cref="ResumeMatchResult"/> with <c>Success = true</c> and a score (0-100),
    /// recommendation tier, matching/missing skills, strengths, and a plain-English summary; or
    /// <c>Success = false</c> with a user-facing <c>Error</c> on failure (missing API key, HTTP
    /// failure, or unparseable response).
    /// </returns>
    public async Task<ResumeMatchResult> MatchAsync(string resumeText, string jobDescription)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return Fail("OpenAI API key is not configured. Run: dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\"");

        // Truncate both inputs to keep the prompt within a reasonable token budget for gpt-4o-mini.
        if (resumeText.Length > 6000)    resumeText     = resumeText[..6000];
        if (jobDescription.Length > 4000) jobDescription = jobDescription[..4000];

        const string systemPrompt =
            "You are a resume-to-job-description matcher. " +
            "Return ONLY a valid JSON object — no markdown, no explanation.";

        var userPrompt = $"""
            Compare this resume against this job description and return a JSON object with exactly these keys:
            - score          (integer 0-100 — overall fit percentage; be realistic and precise)
            - matchingSkills (array of strings — skills present in both resume and JD, max 10)
            - missingSkills  (array of strings — important skills in JD not found in resume, max 8)
            - strengths      (array of strings — 2-4 specific candidate strengths for this role)
            - summary        (string — 2-3 sentence plain-English explanation of the match)

            RESUME:
            {resumeText}

            JOB DESCRIPTION:
            {jobDescription}
            """;

        var body = new
        {
            model = "gpt-4o-mini",
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt   }
            },
            max_tokens = 800,
            temperature = 0.1
        };

        var json = JsonSerializer.Serialize(body, _camel);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _http.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI error {Status}: {Body}", (int)response.StatusCode, raw);
                var msg = (int)response.StatusCode switch
                {
                    401 => "Invalid API key. Set it via dotnet user-secrets.",
                    429 => "OpenAI quota exceeded. Add credits at platform.openai.com/settings/billing.",
                    _   => $"OpenAI returned {(int)response.StatusCode}."
                };
                try
                {
                    using var errDoc = JsonDocument.Parse(raw);
                    if (errDoc.RootElement.TryGetProperty("error", out var err) &&
                        err.TryGetProperty("message", out var errMsg))
                        msg += $" Detail: {errMsg.GetString()}";
                }
                catch { }
                return Fail(msg);
            }

            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";

            return Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI API for resume match");
            return Fail("Request to OpenAI failed. Check your network connection.");
        }
    }

    /// <summary>
    /// Deserializes the model's JSON response into a successful <see cref="ResumeMatchResult"/>
    /// and derives the recommendation tier from the score band. The same 5-tier thresholds (80/
    /// 60/40/20) are mirrored in the front-end JS (Create.cshtml/Index.cshtml TIERS array) so the
    /// color-coded badge always matches this recommendation.
    /// </summary>
    private static ResumeMatchResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var result = new ResumeMatchResult { Success = true };

            if (r.TryGetProperty("score", out var score))
                result.Score = score.ValueKind == JsonValueKind.Number ? Math.Clamp(score.GetInt32(), 0, 100) : 0;

            result.Recommendation = result.Score switch
            {
                >= 80 => "APPLY",
                >= 60 => "APPLY",
                >= 40 => "MAYBE",
                >= 20 => "CONSIDER SKIPPING",
                _     => "SKIP"
            };

            result.Summary        = Str(r, "summary");
            result.MatchingSkills = StrArray(r, "matchingSkills");
            result.MissingSkills  = StrArray(r, "missingSkills");
            result.Strengths      = StrArray(r, "strengths");

            return result;
        }
        catch
        {
            return Fail("Could not parse the AI response. Please try again.");
        }
    }

    /// <summary>Reads a string property, returning null if it's missing or not a string (rather than throwing).</summary>
    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>Reads a string-array property into a List, skipping any null/empty entries.</summary>
    private static List<string> StrArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();
        return arr.EnumerateArray()
            .Select(s => s.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }

    /// <summary>Builds a failed result carrying a user-facing error message.</summary>
    private static ResumeMatchResult Fail(string error) =>
        new() { Success = false, Error = error };
}
