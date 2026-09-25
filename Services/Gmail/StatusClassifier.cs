using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services.Gmail;

/// <summary>What the model is shown: the application on one side, the (trimmed) email on the other.</summary>
public sealed record ClassifierInput(
    string Company,
    string Role,
    ApplicationStatus CurrentStatus,
    string Subject,
    string From,
    string Body,
    DateTime TodayLocal,
    string TimeZoneId);

/// <summary>
/// Parsed model answer. <see cref="SuggestedStatus"/> is null unless the email clearly means
/// Interview, Offer or Rejected; <see cref="InterviewAt"/> keeps the DateTimeKind the model implied
/// (Utc when an offset was given, Unspecified = the user's local time otherwise).
/// </summary>
public sealed record StatusClassification(
    bool Matches,
    ApplicationStatus? SuggestedStatus,
    double Confidence,
    string Summary,
    DateTime? InterviewAt);

public interface IStatusClassifier
{
    /// <summary>Null when the model could not be reached or answered something unparsable.</summary>
    Task<StatusClassification?> ClassifyAsync(ClassifierInput input, CancellationToken ct = default);
}

/// <summary>
/// GPT-4o-mini with a strict JSON contract, following <see cref="JobAnalyzerService"/>'s HTTP pattern.
/// The endpoint is <c>OpenAI:BaseUrl</c> (default api.openai.com) so verification can stub it; the
/// key is <c>OpenAI:ApiKey</c> like every other AI feature. Only <see cref="Parse"/> decides what is
/// accepted: unknown statuses become null, confidence is clamped, summaries are cut to 300 chars.
/// </summary>
public class OpenAiStatusClassifier : IStatusClassifier
{
    public const int SummaryMaxLength = 300;

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly ILogger<OpenAiStatusClassifier> _logger;

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public OpenAiStatusClassifier(HttpClient http, IConfiguration config, ILogger<OpenAiStatusClassifier> logger)
    {
        _http     = http;
        _apiKey   = config["OpenAI:ApiKey"] ?? string.Empty;
        _endpoint = OpenAiEndpoint.ChatCompletions(config);
        _logger   = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && _apiKey != "your-openai-api-key-here";

    public virtual async Task<StatusClassification?> ClassifyAsync(ClassifierInput input, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Status classifier skipped: OpenAI API key is not configured.");
            return null;
        }

        var body = new
        {
            model = "gpt-4o-mini",
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = UserPrompt(input) }
            },
            max_tokens = 220,
            temperature = 0
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body, Camel), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI status classifier returned {Status}.", (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
            return Parse(content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "OpenAI status classifier call failed.");
            return null;
        }
    }

    public const string SystemPrompt =
        "You classify recruiting emails for a job-application tracker. " +
        "Decide whether the email is about the given application and, if it clearly changes its stage, which stage. " +
        "Return ONLY a JSON object with exactly these keys: " +
        "\"matches\" (boolean: the email is from or about this company and this application), " +
        "\"suggestedStatus\" (\"Interview\" when it invites to or schedules an interview or screen, \"Offer\" when it extends an offer, " +
        "\"Rejected\" when it declines the candidate, otherwise null), " +
        "\"confidence\" (number 0-1), " +
        "\"summary\" (one plain sentence, under 200 characters, no names of other people), " +
        "\"interviewAt\" (ISO 8601 date-time of the interview if one is stated, otherwise null). " +
        "Newsletters, job alerts, application receipts and marketing are not status changes: suggestedStatus must be null for them.";

    public static string UserPrompt(ClassifierInput i) => $"""
        Application:
        - Company: {i.Company}
        - Role: {i.Role}
        - Current status: {i.CurrentStatus}
        - Today: {i.TodayLocal:yyyy-MM-dd} (time zone {i.TimeZoneId}); resolve relative dates against it and include the offset if you can.

        Email:
        - Subject: {i.Subject}
        - From: {i.From}
        - Body:
        {i.Body}
        """;

    /// <summary>Strict parse of the model's JSON. Never throws; null for unparsable input.</summary>
    public static StatusClassification? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;

            var matches = r.TryGetProperty("matches", out var m) && m.ValueKind == JsonValueKind.True;

            ApplicationStatus? status = null;
            if (r.TryGetProperty("suggestedStatus", out var s) && s.ValueKind == JsonValueKind.String)
                status = ParseStatus(s.GetString());

            var confidence = 0.0;
            if (r.TryGetProperty("confidence", out var c))
            {
                if (c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var d)) confidence = d;
                else if (c.ValueKind == JsonValueKind.String && double.TryParse(c.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds)) confidence = ds;
            }
            confidence = double.IsNaN(confidence) ? 0 : Math.Clamp(confidence, 0, 1);

            var summary = r.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString()!.Trim() : "";
            summary = System.Text.RegularExpressions.Regex.Replace(summary, @"\s+", " ");
            if (summary.Length > SummaryMaxLength) summary = summary[..(SummaryMaxLength - 1)].TrimEnd() + "…";

            DateTime? interviewAt = null;
            if (r.TryGetProperty("interviewAt", out var ia) && ia.ValueKind == JsonValueKind.String)
                interviewAt = ParseInterviewAt(ia.GetString());

            return new StatusClassification(matches, status, confidence, summary, interviewAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Only the three statuses a suggestion may carry; everything else (Saved, Applied, "Screening", typos) is null.</summary>
    public static ApplicationStatus? ParseStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "interview" => ApplicationStatus.Interview,
            "offer"     => ApplicationStatus.Offer,
            "rejected"  => ApplicationStatus.Rejected,
            _           => null
        };
    }

    /// <summary>ISO 8601 only. With an offset → UTC instant; without → Unspecified (the sync reads it in the user's zone). Date-only values are ignored.</summary>
    public static DateTime? ParseInterviewAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (!raw.Contains('T') && !raw.Contains(' ')) return null;   // a bare date is not an interview time

        if (DateTimeOffset.TryParseExact(raw, new[] { "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset) && (raw.EndsWith('Z') || raw[10..].Contains('+') || raw[10..].Contains('-')))
            return withOffset.UtcDateTime;

        if (DateTime.TryParseExact(raw, new[] { "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        return null;
    }
}
