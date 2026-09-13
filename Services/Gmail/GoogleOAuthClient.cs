using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Gmail.v1;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Services.Gmail;

/// <summary>
/// <see cref="IGoogleOAuthClient"/> backed by Google.Apis.Auth. The only scope ever requested is
/// <c>gmail.readonly</c>; no send or modify scope exists anywhere in the app. No token data store is
/// attached to the flow, so tokens are handed straight back to the caller and nothing is cached by
/// the library.
///
/// Normally this is <see cref="GoogleAuthorizationCodeFlow"/> against Google's endpoints. When
/// <see cref="GmailOptions"/> names stub endpoints (local verification only) the generic
/// <see cref="AuthorizationCodeFlow"/> is used instead, since Google's flow pins its URLs; revocation
/// then goes through a plain POST to the configured revoke endpoint.
/// </summary>
public class GoogleOAuthClient : IGoogleOAuthClient
{
    public static readonly string[] Scopes = { GmailService.Scope.GmailReadonly };

    private readonly AuthorizationCodeFlow _flow;
    private readonly string _authorizationUrl;
    private readonly string _revokeUrl;
    private readonly bool _stubbed;
    private readonly IHttpClientFactory _http;

    public GoogleOAuthClient(IOptions<GoogleOptions> google, IOptions<GmailOptions> gmail, IHttpClientFactory http)
    {
        var g = google.Value;
        var o = gmail.Value;
        _http = http;

        var secrets = new ClientSecrets { ClientId = g.ClientId ?? "", ClientSecret = g.ClientSecret ?? "" };
        _stubbed = !string.IsNullOrWhiteSpace(o.AuthorizationEndpoint) || !string.IsNullOrWhiteSpace(o.TokenEndpoint) || !string.IsNullOrWhiteSpace(o.RevokeEndpoint);

        _authorizationUrl = string.IsNullOrWhiteSpace(o.AuthorizationEndpoint) ? GoogleAuthConsts.OidcAuthorizationUrl : o.AuthorizationEndpoint;
        var tokenUrl      = string.IsNullOrWhiteSpace(o.TokenEndpoint)         ? GoogleAuthConsts.OidcTokenUrl         : o.TokenEndpoint;
        _revokeUrl        = string.IsNullOrWhiteSpace(o.RevokeEndpoint)        ? GoogleAuthConsts.RevokeTokenUrl       : o.RevokeEndpoint;

        _flow = _stubbed
            ? new AuthorizationCodeFlow(new AuthorizationCodeFlow.Initializer(_authorizationUrl, tokenUrl) { ClientSecrets = secrets, Scopes = Scopes })
            : new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = secrets, Scopes = Scopes });
    }

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        var url = new GoogleAuthorizationCodeRequestUrl(new Uri(_authorizationUrl))
        {
            ClientId     = _flow.ClientSecrets.ClientId,
            Scope        = string.Join(" ", Scopes),
            RedirectUri  = redirectUri,
            ResponseType = "code",
            AccessType   = "offline",   // refresh token, so the background sync keeps working
            Prompt       = "consent",   // Google only returns a refresh token when consent is shown
            State        = state
        };
        return url.Build().AbsoluteUri;
    }

    public async Task<GoogleTokens> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        var token = await _flow.ExchangeCodeForTokenAsync("me", code, redirectUri, ct);
        return Map(token);
    }

    public async Task<GoogleTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var token = await _flow.RefreshTokenAsync("me", refreshToken, ct);
        return Map(token);
    }

    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        if (_flow is GoogleAuthorizationCodeFlow googleFlow)
        {
            await googleFlow.RevokeTokenAsync("me", token, ct);
            return;
        }
        // Stub mode: same wire shape Google uses (POST ...?token=).
        using var client = _http.CreateClient();
        using var response = await client.PostAsync(_revokeUrl + (_revokeUrl.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(token), content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    private static GoogleTokens Map(Google.Apis.Auth.OAuth2.Responses.TokenResponse t)
    {
        var issued  = t.IssuedUtc == default ? DateTime.UtcNow : t.IssuedUtc;
        var expires = issued.AddSeconds(t.ExpiresInSeconds ?? 3600);
        return new GoogleTokens(t.AccessToken ?? "", t.RefreshToken, DateTime.SpecifyKind(expires, DateTimeKind.Utc));
    }
}
