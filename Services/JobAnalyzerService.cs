using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternTrackAI.Models.ViewModels;

namespace InternTrackAI.Services;

/// <summary>
/// Parses a pasted job description or job-posting URL into structured fields (company, role,
/// location, salary, key skills) using the OpenAI API. Used by <c>AnalyzerController.Analyze</c>
/// to power the "AI Job Analyzer" panel on the Create Application page, which auto-fills the form
/// from free-text input.
/// </summary>
public class JobAnalyzerService
{
    private readonly HttpClient _http;
    private readonly IHttpClientFactory _factory;
    private readonly string _apiKey;
    private readonly ILogger<JobAnalyzerService> _logger;

    private static readonly JsonSerializerOptions _camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JobAnalyzerService(HttpClient http, IHttpClientFactory factory, IConfiguration config, ILogger<JobAnalyzerService> logger)
    {
        _http = http;
        _factory = factory;
        _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes a job posting and extracts structured data via OpenAI (GPT-4o-mini).
    /// </summary>
    /// <param name="input">
    /// Either the full job description text, or a job posting URL — URLs are detected and the
    /// page is fetched and stripped of HTML server-side before being sent to the model.
    /// </param>
    /// <returns>
    /// A <see cref="JobAnalysisResult"/> with <c>Success = true</c> and the extracted fields on
    /// success, or <c>Success = false</c> with a user-facing <c>Error</c> message if the API key
    /// is missing, the URL can't be fetched, the OpenAI call fails, or the response can't be parsed.
    /// A result object (rather than a thrown exception) is used so the controller can render a
    /// friendly inline error without a try/catch at the call site.
    /// </returns>
    public async Task<JobAnalysisResult> AnalyzeAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return Fail("OpenAI API key is not configured. Run: dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\"");

        string jobDescription = input;

        if (IsUrl(input))
        {
            var fetched = await FetchPageTextAsync(input);
            if (fetched == null || fetched.Length < 50)
                return Fail("Could not fetch the job posting from that URL. The site may require a login or block automated access. Try pasting the job description text instead.");
            jobDescription = fetched;
        }

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
                    401 => "Invalid API key. Set it via dotnet user-secrets.",
                    429 => "OpenAI rate limit or quota exceeded. Add credits at platform.openai.com/settings/billing.",
                    _   => $"OpenAI returned {(int)response.StatusCode}. Check your API key and account."
                };

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

    /// <summary>Returns true if the input looks like an http(s) URL rather than free-text job description.</summary>
    private static bool IsUrl(string input) =>
        input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Downloads a job posting page and returns its visible text, truncated to 8000 characters to
    /// keep the OpenAI prompt small. Spoofs a desktop browser User-Agent because many job boards
    /// block requests from default HttpClient/bot user agents. Returns null on any failure
    /// (non-success status, network error) so the caller can show a friendly fallback message.
    /// </summary>
    private async Task<string?> FetchPageTextAsync(string url)
    {
        try
        {
            using var client = _factory.CreateClient("UrlFetcher");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("URL fetch returned {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync();
            var text = StripHtml(html);

            return text.Length > 8000 ? text[..8000] : text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch URL: {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Reduces raw HTML to plain text suitable for an LLM prompt: drops script/style/nav/header/
    /// footer blocks entirely (noise, not job content), strips remaining tags, decodes common
    /// HTML entities, and collapses excess whitespace.
    /// </summary>
    private static string StripHtml(string html)
    {
        // Drop entire script, style, head, nav, footer blocks
        html = Regex.Replace(html,
            @"<(script|style|head|nav|footer|header)[^>]*>[\s\S]*?<\/\1>",
            " ", RegexOptions.IgnoreCase);

        // Strip remaining tags
        html = Regex.Replace(html, @"<[^>]+>", " ");

        // Decode common entities
        html = html
            .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
            .Replace("&nbsp;", " ").Replace("&quot;", "\"").Replace("&#39;", "'")
            .Replace("&mdash;", "—").Replace("&ndash;", "–").Replace("&bull;", "•");

        // Collapse whitespace
        html = Regex.Replace(html, @"[ \t]{2,}", " ");
        html = Regex.Replace(html, @"\n{3,}", "\n\n");

        return html.Trim();
    }

    /// <summary>Deserializes the model's JSON response into a successful <see cref="JobAnalysisResult"/>.</summary>
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

    /// <summary>Reads a string property, returning null if it's missing or not a string (rather than throwing).</summary>
    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>Builds a failed result carrying a user-facing error message.</summary>
    private static JobAnalysisResult Fail(string error) =>
        new() { Success = false, Error = error };
}
