using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InternTrackAI.Models.ViewModels;

namespace InternTrackAI.Services;

public class JobAnalyzerService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<JobAnalyzerService> _logger;

    private static readonly JsonSerializerOptions _camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JobAnalyzerService(HttpClient http, IConfiguration config, ILogger<JobAnalyzerService> logger)
    {
        _http = http;
        _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
        _logger = logger;
    }

    public async Task<JobAnalysisResult> AnalyzeAsync(string jobDescription)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return Fail("OpenAI API key is not configured. Add it to appsettings.json under OpenAI:ApiKey.");

        var systemPrompt =
            "You are a job description parser. " +
            "Extract structured data and return ONLY a valid JSON object — no markdown, no explanation.";

        var userPrompt = $"""
            Parse this job description and return a JSON object with exactly these keys:
            - companyName  (string or null)
            - roleTitle    (string or null)
            - location     (string or null — city/state/country or "Remote")
            - salary       (string or null — include currency and period, e.g. "$30/hr" or "$80,000/yr")
            - skills       (array of strings — up to 8 key technical skills, empty array if none found)

            Job description:
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
            max_tokens = 400,
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

                var userMessage = (int)response.StatusCode switch
                {
                    401 => "Invalid API key. Check the value in appsettings.json.",
                    429 => "OpenAI rate limit or quota exceeded. Add credits at platform.openai.com/settings/billing.",
                    _   => $"OpenAI returned {(int)response.StatusCode}. Check your API key and account."
                };

                // Surface the OpenAI error detail when available
                try
                {
                    using var errDoc = JsonDocument.Parse(raw);
                    if (errDoc.RootElement.TryGetProperty("error", out var err) &&
                        err.TryGetProperty("message", out var msg))
                        userMessage += $" Detail: {msg.GetString()}";
                }
                catch { /* ignore parse failure */ }

                return Fail(userMessage);
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
            _logger.LogError(ex, "Error calling OpenAI API");
            return Fail("Request to OpenAI failed. Check your network connection.");
        }
    }

    private static JobAnalysisResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var result = new JobAnalysisResult { Success = true };

            result.CompanyName = Str(r, "companyName");
            result.RoleTitle   = Str(r, "roleTitle");
            result.Location    = Str(r, "location");
            result.Salary      = Str(r, "salary");

            if (r.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array)
                result.Skills = skills.EnumerateArray()
                    .Select(s => s.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .ToList();

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

    private static JobAnalysisResult Fail(string error) =>
        new() { Success = false, Error = error };
}
