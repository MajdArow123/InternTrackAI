using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InternTrackAI.Models.ViewModels;

namespace InternTrackAI.Services;

public class ResumeScoreService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<ResumeScoreService> _logger;

    private static readonly JsonSerializerOptions _camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ResumeScoreService(HttpClient http, IConfiguration config, ILogger<ResumeScoreService> logger)
    {
        _http = http;
        _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
        _logger = logger;
    }

    public async Task<ResumeScoreResult> ScoreAsync(string resumeText, IEnumerable<string>? targetRoles = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return Fail("OpenAI API key is not configured.");

        if (resumeText.Length > 7000) resumeText = resumeText[..7000];

        var rolesContext = targetRoles != null && targetRoles.Any()
            ? $" The candidate is targeting roles like: {string.Join(", ", targetRoles)}."
            : string.Empty;

        const string systemPrompt =
            "You are a professional resume reviewer. " +
            "Return ONLY a valid JSON object — no markdown, no explanation.";

        var userPrompt = $"""
            Score this resume and return a JSON object with exactly these keys:
            - score        (integer 0-100 — overall resume quality)
            - summary      (string — 2-3 sentences on overall quality and positioning)
            - strengths    (array of strings — 2-4 specific things done well)
            - improvements (array of strings — 3-5 specific, actionable suggestions to improve the resume){rolesContext}

            RESUME:
            {resumeText}
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
            max_tokens = 700,
            temperature = 0.2
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
                    401 => "Invalid API key.",
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
            _logger.LogError(ex, "Error calling OpenAI API for resume score");
            return Fail("Request to OpenAI failed. Check your network connection.");
        }
    }

    private static ResumeScoreResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var result = new ResumeScoreResult { Success = true };

            if (r.TryGetProperty("score", out var score))
                result.Score = score.ValueKind == JsonValueKind.Number ? Math.Clamp(score.GetInt32(), 0, 100) : 0;

            result.Summary      = Str(r, "summary");
            result.Strengths    = StrArray(r, "strengths");
            result.Improvements = StrArray(r, "improvements");

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

    private static ResumeScoreResult Fail(string error) =>
        new() { Success = false, Error = error };
}
