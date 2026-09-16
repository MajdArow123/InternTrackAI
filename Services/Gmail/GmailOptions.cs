namespace InternTrackAI.Services.Gmail;

/// <summary>
/// Google OAuth client credentials, bound from the <c>Google</c> configuration section
/// (<c>dotnet user-secrets set "Google:ClientId" ...</c> locally, <c>Google__ClientId</c> /
/// <c>Google__ClientSecret</c> on Railway). When either value is missing the whole Gmail
/// integration is switched off: the profile card and dashboard elements are hidden, the
/// Integrations endpoints answer 404 and the background sync never starts.
/// </summary>
public class GoogleOptions
{
    public const string SectionName = "Google";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Optional public origin used verbatim for the OAuth redirect URI, e.g.
    /// <c>https://interntrackai.majdarow.com</c> (<c>Google__RedirectBaseUrl</c> on Railway). Leave it unset while the app answers on more than one hostname — the state cookie is host-scoped, so pinning the origin breaks a flow begun on the other host. When unset
    /// the redirect URI is derived from the request's scheme and host, which behind Railway's proxy are
    /// correct only because the forwarded-headers middleware runs first; set this as a belt-and-braces
    /// override if the derived value ever disagrees with the URI registered in the Google console.
    /// </summary>
    public string? RedirectBaseUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// Behaviour knobs for the Gmail sync, bound from the <c>Gmail</c> section. The endpoint
/// overrides exist so the OAuth and Gmail API traffic can be pointed at a local stub during
/// verification; production never sets them and gets Google's real endpoints.
/// </summary>
public class GmailOptions
{
    public const string SectionName = "Gmail";

    /// <summary>How often the background job syncs every connected account (default 30 minutes).</summary>
    public int SyncIntervalMinutes { get; set; } = 30;

    /// <summary>Messages fetched and classified per account per sync.</summary>
    public int MaxMessagesPerSync { get; set; } = 50;

    /// <summary>Only the first text/plain part is sent to the model, cut to this many characters.</summary>
    public int MaxBodyChars { get; set; } = 4000;

    /// <summary>Test/verification hooks: leave unset for Google's endpoints.</summary>
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? RevokeEndpoint { get; set; }
    public string? ApiBaseUri { get; set; }
}

/// <summary>The single answer to "is the Gmail integration on?", for views, controllers and the hosted service.</summary>
public static class GmailIntegration
{
    public static bool IsConfigured(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["Google:ClientId"]) && !string.IsNullOrWhiteSpace(config["Google:ClientSecret"]);
}
