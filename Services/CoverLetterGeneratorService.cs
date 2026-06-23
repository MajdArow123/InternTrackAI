using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InternTrackAI.Services;

/// <summary>
/// Generates a personalized cover letter via the OpenAI Chat Completions API, combining the
/// target job's company/role/description with the applicant's resume text, skills, target roles,
/// and optional notes. Called by <c>CoverLetterController</c> for both the page flow and the AJAX flow.
/// </summary>
public class CoverLetterGeneratorService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<CoverLetterGeneratorService> _logger;

    // OpenAI's request body uses camelCase fields (e.g. max_tokens is snake_case but the .NET
    // property names here are camelCase already); kept for serializer consistency if more fields are added.
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

    /// <summary>
    /// Builds a prompt from the supplied job and applicant context and asks GPT-4o-mini to write
    /// a 3-4 paragraph cover letter following a fixed format (date, salutation, body, sign-off).
    /// Resume text and job description are truncated to keep the prompt within a reasonable token budget.
    /// </summary>
    /// <returns>
    /// A tuple: <c>Success</c> indicates whether generation succeeded, <c>Content</c> is the
    /// generated letter text (empty on failure), and <c>Error</c> is a user-facing message for
    /// missing API keys, rate limits, or network failures.
    /// </returns>
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

    /// <summary>
    /// Rewrites an existing cover letter draft per the applicant's instructions (e.g. "more concise",
    /// "warmer tone"), optionally anchored to a specific company/role so the rewrite stays on-topic.
    /// Unlike <see cref="GenerateAsync"/>, this never invents new content from a resume — it only
    /// edits what's already on the page.
    /// </summary>
    /// <returns>Same shape as <see cref="GenerateAsync"/>: success flag, rewritten content, error message.</returns>
    public async Task<(bool Success, string Content, string? Error)> ImproveAsync(
        string existingLetter, string company, string role, string instructions)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return (false, "", "OpenAI API key is not configured. Run: dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\"");

        if (existingLetter.Length > 6000) existingLetter = existingLetter[..6000];

        const string systemPrompt =
            "You are an expert cover letter editor. You rewrite existing cover letter drafts to improve " +
            "clarity, tone, grammar, and impact, per the applicant's instructions. Preserve the applicant's " +
            "real facts and claims — never invent new experience, skills, or achievements. Keep the same " +
            "overall structure and length unless told otherwise. Return plain text only — no markdown, no commentary.";

        var context = string.IsNullOrWhiteSpace(company) && string.IsNullOrWhiteSpace(role)
            ? ""
            : $"This letter is for the {role} role at {company}.\n\n";

        var userPrompt = $"""
            {context}EXISTING DRAFT:
            {existingLetter}

            INSTRUCTIONS:
            {(string.IsNullOrWhiteSpace(instructions) ? "Improve clarity, tone, and grammar. Make it sound more polished and confident." : instructions)}

            Return only the rewritten letter text.
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
            temperature = 0.6
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
            _logger.LogError(ex, "Error calling OpenAI API for cover letter improvement");
            return (false, "", "Request to OpenAI failed. Check your network connection.");
        }
    }
}
