using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InternTrackAI.Services;

/// <summary>
/// Estimates a typical compensation range for an internship/job posting via OpenAI, using the
/// role title, company, and location entered on the Create/Edit Application form. Used by
/// <c>SalaryInsightController.Estimate</c> to power the "Salary Insight" card.
/// </summary>
public class SalaryInsightService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<SalaryInsightService> _logger;

    private static readonly JsonSerializerOptions _camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions _read = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SalaryInsightService(HttpClient http, IConfiguration config, ILogger<SalaryInsightService> logger)
    {
        _http   = http;
        _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
        _logger = logger;
    }

    /// <summary>
    /// Asks GPT-4o-mini for a realistic compensation range and one-paragraph rationale for the
    /// given role/company/location, based on general market knowledge — not live data.
    /// </summary>
    /// <returns>
    /// A tuple: <c>Success</c> indicates whether the estimate succeeded, <c>Range</c> is a short
    /// compensation range string (e.g. "$28–$38/hr"), <c>Note</c> is a brief rationale, and
    /// <c>Error</c> is a user-facing message for missing keys, rate limits, or network failures.
    /// </returns>
    public async Task<(bool Success, string Range, string Note, string? Error)> EstimateAsync(
        string role, string company, string? location, string? workMode)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return (false, "", "", "OpenAI API key is not configured.");

        var loc = string.IsNullOrWhiteSpace(location) ? "an unspecified location" : location;
        var mode = string.IsNullOrWhiteSpace(workMode) ? "" : $" ({workMode})";

        const string systemPrompt =
            "You are a compensation research assistant. Estimate typical pay for internships/entry-level " +
            "roles based on general market knowledge of company size, role, and location. Return ONLY a " +
            "valid JSON object — no markdown, no explanation outside the JSON.";

        var userPrompt =
            $"Estimate typical compensation for a {role} internship at {company} in {loc}{mode}.\n\n" +
            "Return this JSON structure with NO other text:\n" +
            "{\"range\": \"...\", \"note\": \"...\"}\n\n" +
            "Rules:\n" +
            "- \"range\": a short pay range string, e.g. \"$28–$38/hr\" or \"$6,000–$8,000/mo\". " +
            "If genuinely unknown, use \"Varies\".\n" +
            "- \"note\": one short sentence (max 25 words) explaining what the estimate is based on " +
            "(company size/industry, role level, location cost of living). Make clear this is an estimate, " +
            "not a guarantee.";

        var body = new
        {
            model = "gpt-4o-mini",
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt   }
            },
            max_tokens  = 200,
            temperature = 0.3
        };

        var json = JsonSerializer.Serialize(body, _camel);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _http.SendAsync(request);
            var raw      = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI error {Status}: {Body}", (int)response.StatusCode, raw);
                var msg = (int)response.StatusCode switch
                {
                    401 => "Invalid API key. Set it via dotnet user-secrets.",
                    429 => "OpenAI quota exceeded. Add credits at platform.openai.com.",
                    _   => $"OpenAI returned {(int)response.StatusCode}."
                };
                return (false, "", "", msg);
            }

            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";

            using var parsed = JsonDocument.Parse(content);
            var root  = parsed.RootElement;
            var range = root.TryGetProperty("range", out var r) ? r.GetString() ?? "" : "";
            var note  = root.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";

            return (true, range, note, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error estimating salary insight");
            return (false, "", "", "Request to OpenAI failed. Check your connection.");
        }
    }
}
