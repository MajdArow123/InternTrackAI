using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InternTrackAI.Services;

/// <summary>
/// Sends real mail through Resend's HTTP API (<c>POST /emails</c> with a bearer token) — no SDK, the same
/// raw-HttpClient shape as the OpenAI services. Registered as <see cref="IAppEmailSender"/> only when
/// <c>Resend:ApiKey</c> is set; otherwise <see cref="ConsoleEmailSender"/> takes its place and links go to
/// the log. <c>Program.cs</c> logs which of the two is live at startup.
///
/// Two properties this class is responsible for, both of them security properties rather than niceties:
///
/// <para><b>It never throws.</b> A failed send is logged and swallowed. The only caller is the
/// forgot-password page, whose response must be byte-identical whether or not the address exists and whether
/// or not delivery worked — an exception surfacing there would turn the page into an account oracle.</para>
///
/// <para><b>It never logs the recipient or the body.</b> Not even from Resend's own error text: a validation
/// failure comes back as <c>{"statusCode":422,"name":"validation_error","message":"Invalid `to` field
/// someone@example.com"}</c>, so <see cref="Redact"/> strips addresses out of the message before it reaches
/// the log. What gets logged is the status, the error name and the redacted message — enough to debug a
/// misconfigured domain or a dead key, with nothing about who was being written to.</para>
///
/// Retries: one. A 5xx, a 429 (Resend's per-second limit, genuinely transient) or a network failure is
/// retried once — after <c>Retry-After</c> when the response names one, else <see cref="RetryDelay"/>. Every
/// other 4xx is a request that will fail again identically (bad key, unverified domain, malformed address),
/// so it fails immediately.
///
/// <para>Both attempts carry the same <c>Idempotency-Key</c>, which is what makes retrying safe at all: the
/// transient case that matters is "Resend accepted the message but the response never came back", and
/// without the key the retry would deliver a second reset email to someone who asked for one.</para>
///
/// <para>The forgot-password POST waits on this, so the whole thing is bounded by <see cref="Budget"/>
/// regardless of how the two attempts and the pause between them divide it up.</para>
/// </summary>
public sealed class ResendEmailSender : IAppEmailSender
{
    public const string DefaultBaseUrl = "https://api.resend.com";

    /// <summary>Pause before the single retry when the response doesn't name one.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on both attempts together, so a Resend outage can't hold the page open for 20s.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    /// <summary>Anything address-shaped. Deliberately greedy about the local part: over-redacting a log line is free.</summary>
    private static readonly Regex AddressRx = new(@"[^\s<>""'()\[\]]+@[^\s<>""'()\[\]]+", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ILogger<ResendEmailSender> _logger;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _from;
    private readonly string? _replyTo;

    public ResendEmailSender(HttpClient http, IConfiguration config, ILogger<ResendEmailSender> logger)
    {
        _http     = http;
        _logger   = logger;
        // Trimmed throughout: Railway variables can carry surrounding whitespace or a trailing newline.
        _apiKey   = config["Resend:ApiKey"]?.Trim() ?? string.Empty;
        _endpoint = (config["Resend:BaseUrl"]?.Trim().TrimEnd('/') ?? DefaultBaseUrl) + "/emails";
        _from     = config["Email:From"]?.Trim() ?? string.Empty;
        var replyTo = config["Email:ReplyTo"]?.Trim();
        _replyTo  = string.IsNullOrEmpty(replyTo) ? null : replyTo;
    }

    /// <summary>Identity's three-argument entry point; Resend derives the text part from the HTML.</summary>
    public Task SendEmailAsync(string email, string subject, string htmlMessage) =>
        SendAsync(email, new EmailMessage(subject, htmlMessage));

    public async Task SendAsync(string to, EmailMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_from))
        {
            // Program.cs only registers this sender when the key is present, so this is belt and braces.
            _logger.LogWarning("Email not sent: Resend:ApiKey or Email:From is not configured.");
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            from     = _from,
            to       = new[] { to },
            subject  = message.Subject,
            html     = message.Html,
            text     = message.Text,
            reply_to = _replyTo
        }, Json);

        // Same key on both attempts: a retry after a lost response must not deliver a second email.
        var idempotencyKey = Guid.NewGuid().ToString("N");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        for (var attempt = 1; ; attempt++)
        {
            var last = attempt == 2;
            try
            {
                // A request message can only be sent once, so it is rebuilt for the retry.
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                request.Headers.Add("Idempotency-Key", idempotencyKey);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(request, budget.Token);
                var raw = await response.Content.ReadAsStringAsync(budget.Token);
                var status = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Sent \"{Subject}\" via Resend (id {Id}).", message.Subject, IdOf(raw));
                    return;
                }

                // 5xx and 429 are worth one more go; every other 4xx would fail the same way twice.
                if ((status >= 500 || status == 429) && !last)
                {
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? RetryDelay, budget.Token);
                    continue;
                }

                var (name, detail) = ErrorOf(raw);
                _logger.LogWarning("Email send failed: Resend returned {Status} {Name}: {Message}", status, name, Redact(detail));
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller gave up; that is not a delivery failure and is not ours to swallow.
                throw;
            }
            catch (Exception ex)
            {
                // Budget spent (this attempt timed out and there is no time for another): stop here.
                if (budget.IsCancellationRequested)
                {
                    _logger.LogWarning("Email send failed: Resend did not answer within {Seconds}s.", Budget.TotalSeconds);
                    return;
                }

                if (!last)
                {
                    await Task.Delay(RetryDelay, budget.Token);
                    continue;
                }

                _logger.LogWarning("Email send failed: request to Resend threw {Error}.", ex.GetType().Name);
                return;
            }
        }
    }

    /// <summary>Replaces every address-shaped run with a placeholder. Resend echoes the recipient in validation errors.</summary>
    public static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? "" : AddressRx.Replace(text, "[address]");

    /// <summary>The <c>name</c> and <c>message</c> of a Resend error body, or placeholders when it isn't one.</summary>
    private static (string Name, string Message) ErrorOf(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            return (name ?? "unknown_error", message ?? "");
        }
        catch (JsonException)
        {
            return ("unknown_error", "");
        }
    }

    /// <summary>The id Resend assigns the queued message, for tracing a send in its dashboard.</summary>
    private static string IdOf(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "?" : "?";
        }
        catch (JsonException)
        {
            return "?";
        }
    }
}
