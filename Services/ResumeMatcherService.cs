using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InternTrackAI.Models.ViewModels;
using UglyToad.PdfPig;

namespace InternTrackAI.Services;

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

    public async Task<ResumeMatchResult> MatchAsync(string resumeText, string jobDescription)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return Fail("OpenAI API key is not configured. Add it to appsettings.json under OpenAI:ApiKey.");

        if (resumeText.Length > 6000)    resumeText     = resumeText[..6000];
        if (jobDescription.Length > 4000) jobDescription = jobDescription[..4000];

        const string systemPrompt =
            "You are a resume-to-job-description matcher. " +
            "Return ONLY a valid JSON object — no markdown, no explanation.";

        var userPrompt = $"""
            Compare this resume against this job description and return a JSON object with exactly these keys:
            - score          (integer 0-100 — overall fit percentage)
            - recommendation (string — exactly one of: "Apply", "Maybe", "Don't Apply")
            - matchingSkills (array of strings — skills present in both resume and JD, max 10)
            - missingSkills  (array of strings — skills in JD not found in resume, max 8)
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
                    401 => "Invalid API key. Check the value in appsettings.json.",
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

    private static ResumeMatchResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var result = new ResumeMatchResult { Success = true };

            if (r.TryGetProperty("score", out var score))
                result.Score = score.ValueKind == JsonValueKind.Number ? Math.Clamp(score.GetInt32(), 0, 100) : 0;

            result.Recommendation = Str(r, "recommendation") ?? string.Empty;
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

    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

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

    private static ResumeMatchResult Fail(string error) =>
        new() { Success = false, Error = error };
}
