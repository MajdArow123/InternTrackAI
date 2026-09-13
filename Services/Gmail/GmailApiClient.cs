using System.Net.Mail;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Services.Gmail;

/// <summary>
/// <see cref="IGmailClient"/> over Google.Apis.Gmail.v1. Every call is a read (<c>users.getProfile</c>,
/// <c>users.messages.list</c>, <c>users.messages.get</c>); the service is built per call from the bare
/// access token so no credential object outlives the request.
/// </summary>
public class GmailApiClient : IGmailClient
{
    private readonly GmailOptions _options;

    public GmailApiClient(IOptions<GmailOptions> options) => _options = options.Value;

    private GmailService Service(string accessToken)
    {
        var init = new BaseClientService.Initializer
        {
            HttpClientInitializer = GoogleCredential.FromAccessToken(accessToken),
            ApplicationName       = "InternTrackAI"
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiBaseUri)) init.BaseUri = _options.ApiBaseUri;
        return new GmailService(init);
    }

    public async Task<string?> GetProfileEmailAsync(string accessToken, CancellationToken ct = default)
    {
        using var svc = Service(accessToken);
        var profile = await svc.Users.GetProfile("me").ExecuteAsync(ct);
        return profile.EmailAddress;
    }

    public async Task<IReadOnlyList<string>> ListMessageIdsAsync(string accessToken, string query, int max, CancellationToken ct = default)
    {
        using var svc = Service(accessToken);
        var request = svc.Users.Messages.List("me");
        request.Q          = query;
        request.MaxResults = Math.Clamp(max, 1, 500);
        var response = await request.ExecuteAsync(ct);
        return response.Messages?.Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)).ToList() ?? new List<string>();
    }

    public async Task<GmailMessage?> GetMessageAsync(string accessToken, string id, int maxBodyChars, CancellationToken ct = default)
    {
        using var svc = Service(accessToken);
        var request = svc.Users.Messages.Get("me", id);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;

        Message msg;
        try { msg = await request.ExecuteAsync(ct); }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound) { return null; }

        return Map(msg, maxBodyChars);
    }

    /// <summary>Pure mapping from the API shape, exposed for tests: headers, snippet, first text/plain part, internal date.</summary>
    public static GmailMessage Map(Message msg, int maxBodyChars)
    {
        var headers = msg.Payload?.Headers;
        string Header(string name) => headers?.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

        DateTime? date = msg.InternalDate.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(msg.InternalDate.Value).UtcDateTime : null;
        if (date is null && DateTime.TryParse(Header("Date"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            date = parsed;

        var body = FirstTextPlain(msg.Payload) ?? "";
        if (body.Length > maxBodyChars) body = body[..maxBodyChars];

        return new GmailMessage(msg.Id ?? "", Header("Subject"), Header("From"), date, msg.Snippet ?? "", body);
    }

    private static string? FirstTextPlain(MessagePart? part)
    {
        if (part is null) return null;
        if (string.Equals(part.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(part.Body?.Data))
            return DecodeBase64Url(part.Body.Data);
        if (part.Parts is null) return null;
        foreach (var child in part.Parts)
        {
            var found = FirstTextPlain(child);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>Gmail bodies are base64url without padding.</summary>
    public static string DecodeBase64Url(string data)
    {
        var s = data.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch (FormatException) { return ""; }
    }

    /// <summary>"Jane Doe &lt;jane@stripe.com&gt;" → "stripe.com" (lower-case); null when there is no parsable address.</summary>
    public static string? SenderDomain(string from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;
        try
        {
            var addr = new MailAddress(from.Trim());
            return addr.Host.ToLowerInvariant();
        }
        catch (FormatException)
        {
            var at = from.LastIndexOf('@');
            if (at < 0 || at == from.Length - 1) return null;
            var host = from[(at + 1)..].Trim().TrimEnd('>').Trim();
            return host.Length == 0 ? null : host.ToLowerInvariant();
        }
    }
}
