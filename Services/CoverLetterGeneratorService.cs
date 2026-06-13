using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InternTrackAI.Services;

public class CoverLetterGeneratorService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<CoverLetterGeneratorService> _logger;

    private static readonly JsonSerializerOptions _camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CoverLetterGeneratorService(HttpClient http, IConfiguration config,
        ILogger<CoverLetterGeneratorService> logger)
    {
        _http    = http;
        _apiKey  = config["OpenAI:ApiKey"] ?? string.Empty;
        _logger  = logger;
    }

    public async Task<(bool Success, string Content, string? Error)> GenerateAsync(
        string company, string role, string jobDescription,
        string resumeText, string fullName, string skills,
        string targetRoles, string extraNotes)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return (false, "", "OpenAI API key is not configured. Run: dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\"");

        if (resumeText.Length   > 5000) resumeText    = resumeText[..5000];
        if (jobDescription.Length > 4000) jobDescription = jobDescription[..4000];

        const string systemPrompt =
            "You are a professional cover letter writer specializing in internship and entry-level applications. " +
            "Write polished, specific cover letters that reference real details from the job description and resume. " +
            "Never use placeholder text. Use formal but natural language. Return plain text only — no markdown.";

        var today = DateTime.Now.ToString("MMMM d, yyyy");
        var name  = string.IsNullOrWhiteSpace(fullName) ? "the applicant" : fullName;

        var userPrompt = $"""
            Write a professional cover letter using the information below.

            APPLICANT:
            Name: {name}
            Skills: {(string.IsNullOrWhiteSpace(skills) ? "see resume" : skills)}
            Target Roles: {(string.IsNullOrWhiteSpace(targetRoles) ? "internship positions" : targetRoles)}

            RESUME:
            {(string.IsNullOrWhiteSpace(resumeText) ? "(no resume provided)" : resumeText)}

            JOB:
            Company: {company}
            Role: {role}
            Description:
            {(string.IsNullOrWhiteSpace(jobDescription) ? "(no description provided)" : jobDescription)}

            {(string.IsNullOrWhiteSpace(extraNotes) ? "" : $"NOTES FROM APPLICANT:\n{extraNotes}\n")}
            FORMAT RULES:
            - First line: {today}
            - Blank line, then: Dear Hiring Manager,
            - 3-4 body paragraphs separated by blank lines
            - Opening paragraph: express genuine, specific interest in the {role} role at {company}
            - Middle paragraphs: reference 2-3 specific requirements from the job description and match them to resume evidence
            - Closing paragraph: thank them, request an interview, state availability
            - Sign-off: Sincerely, then a blank line, then {name}
            - Do not include a mailing address header
            - Plain text only, no bullet points, no markdown
            """;

        var body = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt   }
            },
            max_tokens = 1000,
            temperature = 0.7
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
                    429 => "OpenAI quota exceeded. Add credits at platform.openai.com/settings/billing.",
                    _   => $"OpenAI returned {(int)response.StatusCode}."
                };
                try
                {
                    using var errDoc = JsonDocument.Parse(raw);
                    if (errDoc.RootElement.TryGetProperty("error", out var err) &&
                        err.TryGetProperty("message", out var errMsg))
                        msg += $" {errMsg.GetString()}";
                }
                catch { }
                return (false, "", msg);
            }

            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            return (true, content.Trim(), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI API for cover letter generation");
            return (false, "", "Request to OpenAI failed. Check your network connection.");
        }
    }
}
