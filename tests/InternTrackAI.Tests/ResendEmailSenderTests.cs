using System.Net;
using System.Text;
using System.Text.Json;
using InternTrackAI.Services;
using InternTrackAI.Tests.Integration;
using Microsoft.Extensions.Configuration;

namespace InternTrackAI.Tests;

/// <summary>
/// The Resend sender against a scripted <see cref="HttpMessageHandler"/> — never the real API. Covers the
/// wire format, the one-retry rule, and the two properties the forgot-password flow leans on: a failed send
/// never throws, and nothing identifying ever reaches the log.
/// </summary>
public class ResendEmailSenderTests
{
    private const string Key  = "re_test-not-real";
    private const string From = "InternTrackAI <noreply@majdarow.com>";
    private const string To   = "recipient@example.test";

    /// <summary>Answers each request from <see cref="Replies"/> (last one repeats) and records what it was sent.</summary>
    private sealed class FakeResend : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public List<Func<HttpResponseMessage>> Replies { get; } = new();

        public static HttpResponseMessage Ok(string id = "9f1c2d3e-0000-4444-8888-aaaabbbbcccc") =>
            new(HttpStatusCode.OK) { Content = new StringContent($"{{\"id\":\"{id}\"}}", Encoding.UTF8, "application/json") };

        public static Func<HttpResponseMessage> Error(HttpStatusCode status, string name, string message) => () =>
            new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { statusCode = (int)status, name, message }), Encoding.UTF8, "application/json")
            };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(request);
            if (Replies.Count == 0) return Ok();
            return Replies[Math.Min(Requests.Count - 1, Replies.Count - 1)]();
        }

        public JsonElement Payload(int index = 0) => JsonDocument.Parse(Bodies[index]).RootElement;
    }

    private static (ResendEmailSender Sender, FakeResend Handler, CapturingLogger<ResendEmailSender> Log) Build(
        params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["Resend:ApiKey"] = Key,
            ["Email:From"]    = From,
        };
        foreach (var (k, v) in settings) values[k] = v;

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var handler = new FakeResend();
        var log = new CapturingLogger<ResendEmailSender>();
        // Zero timeout on the retry pause would be nicer, but one second keeps the suite honest about the real path.
        return (new ResendEmailSender(new HttpClient(handler), config, log), handler, log);
    }

    private static EmailMessage Message() =>
        new("Reset your InternTrackAI password", "<!doctype html><html><body>hello</body></html>", "hello in plain text");

    // ── Wire format ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Posts_to_the_emails_endpoint_with_the_bearer_token()
    {
        var (sender, handler, _) = Build();
        await sender.SendAsync(To, Message());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.resend.com/emails", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Key, request.Headers.Authorization.Parameter);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Payload_carries_from_to_subject_html_and_text()
    {
        var (sender, handler, _) = Build();
        var message = Message();
        await sender.SendAsync(To, message);

        var payload = handler.Payload();
        Assert.Equal(From, payload.GetProperty("from").GetString());
        Assert.Equal(message.Subject, payload.GetProperty("subject").GetString());
        Assert.Equal(message.Html, payload.GetProperty("html").GetString());
        Assert.Equal(message.Text, payload.GetProperty("text").GetString());

        // Resend takes `to` as an array even for one recipient.
        var recipients = payload.GetProperty("to");
        Assert.Equal(JsonValueKind.Array, recipients.ValueKind);
        Assert.Equal(To, Assert.Single(recipients.EnumerateArray().Select(e => e.GetString())));
    }

    [Fact]
    public async Task Text_is_omitted_when_the_message_has_none_so_Resend_derives_it()
    {
        var (sender, handler, _) = Build();
        await sender.SendEmailAsync(To, "Subject", "<p>body</p>");

        Assert.False(handler.Payload().TryGetProperty("text", out _));
    }

    [Fact]
    public async Task Reply_to_is_omitted_unless_configured()
    {
        var (bare, bareHandler, _) = Build();
        await bare.SendAsync(To, Message());
        Assert.False(bareHandler.Payload().TryGetProperty("reply_to", out _));

        var (configured, handler, _) = Build(("Email:ReplyTo", "majd@example.test"));
        await configured.SendAsync(To, Message());
        Assert.Equal("majd@example.test", handler.Payload().GetProperty("reply_to").GetString());
    }

    [Fact]
    public async Task Resend_BaseUrl_overrides_the_endpoint()
    {
        var (sender, handler, _) = Build(("Resend:BaseUrl", "http://localhost:5399/"));
        await sender.SendAsync(To, Message());

        Assert.Equal("http://localhost:5399/emails", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_key_or_sender_address_is_missing()
    {
        var (noKey, noKeyHandler, _) = Build(("Resend:ApiKey", ""));
        await noKey.SendAsync(To, Message());
        Assert.Empty(noKeyHandler.Requests);

        var (noFrom, noFromHandler, _) = Build(("Email:From", ""));
        await noFrom.SendAsync(To, Message());
        Assert.Empty(noFromHandler.Requests);
    }

    // ── Retry ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "application_error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "service_unavailable")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_exceeded")]
    public async Task A_transient_failure_is_retried_exactly_once(HttpStatusCode status, string name)
    {
        var (sender, handler, _) = Build();
        handler.Replies.Add(FakeResend.Error(status, name, "try again"));

        await sender.SendAsync(To, Message());

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_transient_failure_that_clears_on_the_retry_succeeds()
    {
        var (sender, handler, log) = Build();
        handler.Replies.Add(FakeResend.Error(HttpStatusCode.InternalServerError, "application_error", "boom"));
        handler.Replies.Add(() => FakeResend.Ok("retried-id"));

        await sender.SendAsync(To, Message());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(log.Lines, l => l.Contains("retried-id"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("failed"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "missing_api_key")]
    [InlineData(HttpStatusCode.Forbidden, "validation_error")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "validation_error")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    public async Task A_non_transient_4xx_is_never_retried(HttpStatusCode status, string name)
    {
        var (sender, handler, _) = Build();
        handler.Replies.Add(FakeResend.Error(status, name, "no"));

        await sender.SendAsync(To, Message());

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_retry_reuses_the_same_idempotency_key_so_it_cannot_double_send()
    {
        var (sender, handler, _) = Build();
        handler.Replies.Add(FakeResend.Error(HttpStatusCode.InternalServerError, "application_error", "boom"));

        await sender.SendAsync(To, Message());

        var keys = handler.Requests.Select(r => r.Headers.GetValues("Idempotency-Key").Single()).ToList();
        Assert.Equal(2, keys.Count);
        Assert.NotEmpty(keys[0]);
        Assert.Equal(keys[0], keys[1]);   // the retry is the SAME message, not a second one

        // A genuinely separate send is a separate message.
        await sender.SendAsync(To, Message());
        Assert.NotEqual(keys[0], handler.Requests[2].Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task A_network_failure_is_retried_once_and_then_swallowed()
    {
        var (sender, handler, log) = Build();
        handler.Replies.Add(() => throw new HttpRequestException("connection refused"));

        await sender.SendAsync(To, Message());   // must not throw

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(log.Lines, l => l.Contains("HttpRequestException"));
    }

    // ── Never throws, never leaks ────────────────────────────────────────────

    [Fact]
    public async Task A_failed_send_never_throws()
    {
        var (server, serverHandler, _) = Build();
        serverHandler.Replies.Add(FakeResend.Error(HttpStatusCode.InternalServerError, "application_error", "boom"));
        await server.SendAsync(To, Message());

        var (rejected, rejectedHandler, _) = Build();
        rejectedHandler.Replies.Add(FakeResend.Error(HttpStatusCode.UnprocessableEntity, "validation_error", "bad"));
        await rejected.SendAsync(To, Message());

        var (thrown, thrownHandler, _) = Build();
        thrownHandler.Replies.Add(() => throw new InvalidOperationException("kaboom"));
        await thrown.SendAsync(To, Message());

        // Reaching here without an exception is the assertion; the unparseable body below is the last variant.
        var (garbage, garbageHandler, log) = Build();
        garbageHandler.Replies.Add(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html")
        });
        await garbage.SendAsync(To, Message());
        Assert.Contains(log.Lines, l => l.Contains("unknown_error"));
    }

    [Fact]
    public async Task A_failure_logs_the_status_and_error_name_but_not_the_recipient_or_body()
    {
        var (sender, handler, log) = Build();
        handler.Replies.Add(FakeResend.Error(HttpStatusCode.UnprocessableEntity, "validation_error", "the domain is not verified"));

        await sender.SendAsync(To, Message());

        var line = Assert.Single(log.Lines);
        Assert.Contains("422", line);
        Assert.Contains("validation_error", line);
        Assert.Contains("the domain is not verified", line);
        Assert.DoesNotContain(To, line);
        Assert.DoesNotContain("hello", line);            // neither the html nor the text part
        Assert.DoesNotContain(Key, line);
    }

    [Fact]
    public async Task Addresses_inside_Resends_own_error_message_are_redacted()
    {
        // Resend really does echo the recipient back in validation errors.
        var (sender, handler, log) = Build();
        handler.Replies.Add(FakeResend.Error(HttpStatusCode.UnprocessableEntity, "validation_error", $"Invalid `to` field {To}"));

        await sender.SendAsync(To, Message());

        var line = Assert.Single(log.Lines);
        Assert.DoesNotContain(To, line);
        Assert.DoesNotContain("@", line);
        Assert.Contains("[address]", line);
    }

    [Fact]
    public async Task A_successful_send_logs_the_subject_and_id_but_not_the_recipient()
    {
        var (sender, _, log) = Build();
        await sender.SendAsync(To, Message());

        var line = Assert.Single(log.Lines);
        Assert.Contains("Reset your InternTrackAI password", line);
        Assert.Contains("9f1c2d3e", line);
        Assert.DoesNotContain(To, line);
    }

    [Theory]
    [InlineData("Invalid `to` field someone@example.com", "Invalid `to` field [address]")]
    [InlineData("two: a@b.test and c@d.test", "two: [address] and [address]")]
    [InlineData("nothing to redact", "nothing to redact")]
    [InlineData(null, "")]
    public void Redact_removes_every_address_shaped_run(string? input, string expected) =>
        Assert.Equal(expected, ResendEmailSender.Redact(input));
}
