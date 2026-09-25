using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InternTrackAI.Services;

/// <summary>
/// Scores one practice answer. <b>The app's only answer-feedback path</b> — both the practice page and
/// the interview prep page go through it.
/// </summary>
/// <remarks>
/// Replaces <c>InterviewPrepService.CritiqueAnswerAsync</c>, which returned three loose paragraphs of
/// plain text and persisted nothing. One prompt rather than two is the same principle that merged the
/// question stores in Phase 3: a second, drifting copy of "how we judge an answer" is worse than none.
/// Since 2026-09-25 the prep page answers through <c>/Practice/SubmitAnswer</c> too, so every caller
/// gets the structured verdict and nothing flattens it to text.
/// </remarks>
public class AnswerFeedbackService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly ILogger<AnswerFeedbackService> _logger;

    private static readonly JsonSerializerOptions _camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AnswerFeedbackService(HttpClient http, IConfiguration config, ILogger<AnswerFeedbackService> logger)
    {
        _http = http;
        _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
        // Honours OpenAI:BaseUrl, like the generator, so the whole submit path can be driven against a
        // stub locally without spending anything.
        _endpoint = OpenAiEndpoint.ChatCompletions(config);
        _logger = logger;
    }

    /// <summary>
    /// Scores the answer. <c>Success = false</c> carries a user-facing <c>Error</c>; on success the
    /// feedback is non-null and its raw JSON is returned for storage.
    /// </summary>
    public virtual async Task<(bool Success, AnswerFeedback? Feedback, string? RawJson, string? Error)> EvaluateAsync(
        AnswerContext context, string? profileContext = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return (false, null, null, "OpenAI API key is not configured.");

        var body = new
        {
            model = "gpt-4o-mini",
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = AnswerFeedbackPrompt.SystemPrompt },
                new { role = "user",   content = AnswerFeedbackPrompt.Build(context, profileContext) }
            },
            max_tokens = 800,
            // Low, unlike the generator's 0.8: this is a judgement, and the same answer submitted twice
            // should not swing two points because variety was asked for.
            temperature = 0.3
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body, _camel), Encoding.UTF8, "application/json");

        try
        {
            var response = await _http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Answer feedback failed: OpenAI returned {Status}.", (int)response.StatusCode);
                return (false, null, null, (int)response.StatusCode switch
                {
                    401 => "Invalid API key.",
                    429 => "OpenAI quota exceeded. Add credits at platform.openai.com/settings/billing.",
                    _   => $"OpenAI returned {(int)response.StatusCode}."
                });
            }

            using var doc = JsonDocument.Parse(raw);
            var content = PromptData.UnwrapFence(doc.RootElement
                .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}");

            // FromJson never throws, so a reply that is JSON but the wrong shape still scores rather than
            // erroring — the user typed a paragraph and is owed feedback, not a formatting complaint.
            return (true, AnswerFeedback.FromJson(content), content, null);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Answer feedback returned malformed JSON.");
            return (false, null, null, "The coach returned an unexpected format. Try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI for answer feedback.");
            return (false, null, null, "Request to OpenAI failed. Check your connection.");
        }
    }
}
