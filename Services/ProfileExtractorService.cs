using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InternTrackAI.Services;

/// <summary>What the extractor pulled out of a resume; <c>Success = false</c> carries a user-facing <c>Error</c>.</summary>
public sealed record ProfileExtraction(bool Success, string? FullName, List<string> Skills, List<string> TargetRoles, string? Error)
{
    public static ProfileExtraction Failed(string error) => new(false, null, new(), new(), error);
}

/// <summary>
/// Pulls a full name, skills, and likely target roles out of resume text. The OpenAI-backed
/// implementation is <see cref="ProfileExtractorService"/>; tests substitute a scripted one.
/// </summary>
public interface IProfileExtractor
{
    Task<ProfileExtraction> ExtractAsync(string resumeText, CancellationToken ct = default);
}

/// <summary>
/// Extracts career-portfolio fields (full name, skills, target job titles) from raw resume text
/// via the OpenAI API. Runs automatically after every resume upload and behind the Profile page's
/// "Analyze with AI" button (see <see cref="ProfileAutoFillService"/>), so the user doesn't retype
/// information already on their resume. The endpoint is <c>OpenAI:BaseUrl</c> (default
/// api.openai.com) so local verification can point it at a stub.
/// </summary>
public class ProfileExtractorService : IProfileExtractor
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly ILogger<ProfileExtractorService> _logger;

    private static readonly JsonSerializerOptions _camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ProfileExtractorService(HttpClient http, IConfiguration config, ILogger<ProfileExtractorService> logger)
    {
        _http = http;
        _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
        _endpoint = (config["OpenAI:BaseUrl"]?.TrimEnd('/') ?? "https://api.openai.com") + "/v1/chat/completions";
        _logger = logger;
    }

    /// <summary>
    /// Asks GPT-4o-mini to read a resume and pull out the candidate's full name, a deduplicated
    /// list of technical/professional skills, and likely target job titles (inferred from the most
    /// recent experience/objective section, not just listed verbatim).
    /// </summary>
    /// <param name="resumeText">Plain text extracted from the candidate's resume PDF.</param>
    /// <returns>
    /// <see cref="ProfileExtraction"/>: <c>Success</c> indicates whether extraction succeeded,
    /// <c>FullName</c>/<c>Skills</c>/<c>TargetRoles</c> hold the parsed fields (empty/null if not
    /// found), and <c>Error</c> is a user-facing message for missing keys, rate limits, or network failures.
    /// </returns>
    public async Task<ProfileExtraction> ExtractAsync(string resumeText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return ProfileExtraction.Failed("OpenAI API key is not configured.");

        if (resumeText.Length > 6000) resumeText = resumeText[..6000];

        const string systemPrompt =
            "You extract structured profile data from resumes. " +
            "Return ONLY a valid JSON object — no markdown, no explanation.";

        var userPrompt = $"""
            Read this resume and return a JSON object with exactly these keys:
            - fullName    (string or null — the candidate's full name)
            - skills      (array of strings — up to 12 technical/professional skills found on the resume)
            - targetRoles (array of strings — up to 3 job titles this candidate is best suited for,
              based on their most recent experience/education, e.g. "Backend Developer Intern")

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
            max_tokens  = 500,
            temperature = 0.2
        };

        var json = JsonSerializer.Serialize(body, _camel);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _http.SendAsync(request, ct);
            var raw      = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI error {Status}: {Body}", (int)response.StatusCode, raw);
                var msg = (int)response.StatusCode switch
                {
                    401 => "Invalid API key.",
                    429 => "OpenAI quota exceeded. Add credits at platform.openai.com/settings/billing.",
                    _   => $"OpenAI returned {(int)response.StatusCode}."
                };
                return ProfileExtraction.Failed(msg);
            }

            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";

            using var resultDoc = JsonDocument.Parse(content);
            var r = resultDoc.RootElement;

            string? fullName = r.TryGetProperty("fullName", out var fn) && fn.ValueKind == JsonValueKind.String
                ? fn.GetString() : null;

            var skills = StrArray(r, "skills");
            var roles  = StrArray(r, "targetRoles");

            return new ProfileExtraction(true, fullName, skills, roles, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI API for profile extraction");
            return ProfileExtraction.Failed("Request to OpenAI failed. Check your network connection.");
        }
    }

    private static List<string> StrArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();
        return arr.EnumerateArray()
            .Select(s => s.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct()
            .ToList();
    }
}
