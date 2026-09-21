using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services;

/// <summary>
/// Generates a set of tailored interview questions (Technical/Behavioral/Company-Specific) via
/// the OpenAI Chat Completions API, using the target job's company/role/description plus the
/// candidate's resume text and skills. Called by <c>InterviewPrepController.Generate</c>.
/// </summary>
public class InterviewPrepService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<InterviewPrepService> _logger;

    private static readonly JsonSerializerOptions _camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Tolerates casing differences when parsing the model's JSON response.
    private static readonly JsonSerializerOptions _read = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public InterviewPrepService(HttpClient http, IConfiguration config,
        ILogger<InterviewPrepService> logger)
    {
        _http   = http;
        _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
        _logger = logger;
    }

    /// <summary>
    /// Asks GPT-4o-mini for 8-10 interview questions (3-4 Technical, 3-4 Behavioral, 2 Company-Specific)
    /// tailored to the given role/company/job description/resume, each with a short answering tip.
    /// The prompt instructs the model to return raw JSON; any markdown code-fence wrapping the model
    /// sometimes adds is stripped before parsing, since the model doesn't always follow the no-markdown instruction exactly.
    /// </summary>
    /// <returns>
    /// A tuple: <c>Success</c> indicates whether generation succeeded, <c>Questions</c> is the parsed
    /// list (empty on failure), and <c>Error</c> is a user-facing message for missing keys, rate
    /// limits, or network failures.
    /// </returns>
    /// <remarks>Virtual so integration tests can subclass it, the same reason <see cref="JobAnalyzerService.AnalyzeAsync"/> is.</remarks>
    public virtual async Task<(bool Success, List<GeneratedQuestion> Questions, string? Error)> GenerateAsync(
        string company, string role, string jobDescription, string resumeText, string skills,
        string? profileContext = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return (false, new(), "OpenAI API key is not configured.");

        if (resumeText.Length    > 4000) resumeText    = resumeText[..4000];
        if (jobDescription.Length > 3000) jobDescription = jobDescription[..3000];

        const string systemPrompt =
            "You are an expert interview coach. Return ONLY a valid JSON array — " +
            "no markdown, no code fences, no explanation before or after the JSON.";

        var jobDesc = string.IsNullOrWhiteSpace(jobDescription) ? "(none provided)" : jobDescription;
        var resume  = string.IsNullOrWhiteSpace(resumeText)   ? "(none provided)" : resumeText;
        var skillStr = string.IsNullOrWhiteSpace(skills)      ? "(none provided)" : skills;

        var userPrompt =
            UserContextBuilder.Prefix(profileContext) +
            $"Generate 8–10 interview questions for a candidate applying for {role} at {company}.\n\n" +
            $"JOB DESCRIPTION:\n{jobDesc}\n\n" +
            $"CANDIDATE RESUME:\n{resume}\n\n" +
            $"CANDIDATE SKILLS: {skillStr}\n\n" +
            "Return this JSON structure with NO other text:\n" +
            "[\n" +
            "  {\"category\": \"Technical\", \"question\": \"...\", \"tip\": \"...\"},\n" +
            "  {\"category\": \"Behavioral\", \"question\": \"...\", \"tip\": \"...\"},\n" +
            "  {\"category\": \"Company-Specific\", \"question\": \"...\", \"tip\": \"...\"}\n" +
            "]\n\n" +
            "Rules:\n" +
            $"- Category must be exactly \"Technical\", \"Behavioral\", or \"Company-Specific\"\n" +
            "- \"Technical\" means role-specific knowledge in the candidate's own field, whatever that field is: " +
            "clinical questions for a nurse, accounting standards for an accountant, code for a developer. " +
            "Never default to software questions for a non-software role.\n" +
            "- Include 3–4 Technical, 3–4 Behavioral, 2 Company-Specific questions\n" +
            $"- Questions must be specific to {company} and this {role} role — not generic\n" +
            "- Tips: 1–2 sentences on how to approach the question";

        var body = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt   }
            },
            max_tokens  = 2000,
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
                    429 => "OpenAI quota exceeded. Add credits at platform.openai.com.",
                    _   => $"OpenAI returned {(int)response.StatusCode}."
                };
                return (false, new(), msg);
            }

            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "[]";

            // Strip markdown code fences if present
            content = content.Trim();
            if (content.StartsWith("```json")) content = content[7..];
            else if (content.StartsWith("```")) content = content[3..];
            if (content.EndsWith("```")) content = content[..^3];
            content = content.Trim();

            var parsed = JsonSerializer.Deserialize<List<QuestionReply>>(content, _read) ?? new();

            // The model's category spelling is resolved here, once, so nothing downstream handles a
            // raw string. A blank question is dropped rather than stored as an empty card.
            var questions = parsed
                .Where(q => !string.IsNullOrWhiteSpace(q.Question))
                .Select(q => new GeneratedQuestion(
                    QuestionCategories.Parse(q.Category),
                    q.Question.Trim(),
                    string.IsNullOrWhiteSpace(q.Tip) ? null : q.Tip.Trim()))
                .ToList();

            return (true, questions, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating interview prep");
            return (false, new(), "Request to OpenAI failed. Check your connection.");
        }
    }

}

/// <summary>One question straight off the model, with its category already resolved to the enum.</summary>
/// <remarks>
/// Not an entity: <see cref="Controllers.InterviewPrepController"/> turns these into
/// <see cref="Models.PracticeQuestion"/> rows, which is where the hash and the ownership live.
/// </remarks>
public sealed record GeneratedQuestion(Models.Enums.QuestionCategory Category, string Question, string? Tip);

/// <summary>The raw JSON shape the model replies in; <c>category</c> arrives as free text.</summary>
internal sealed record QuestionReply(string? Category, string Question, string? Tip);
