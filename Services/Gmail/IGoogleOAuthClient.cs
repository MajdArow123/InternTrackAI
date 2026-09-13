namespace InternTrackAI.Services.Gmail;

/// <summary>Result of a code exchange or refresh. <see cref="RefreshToken"/> is null on refresh responses.</summary>
public sealed record GoogleTokens(string AccessToken, string? RefreshToken, DateTime ExpiresAtUtc);

/// <summary>
/// The OAuth 2.0 side of the Gmail integration (authorization URL, code exchange, refresh, revoke).
/// <see cref="GoogleOAuthClient"/> talks to Google through Google.Apis.Auth; tests register a fake.
/// </summary>
public interface IGoogleOAuthClient
{
    /// <summary>Google consent-screen URL requesting <c>gmail.readonly</c> with access_type=offline, prompt=consent and the given state.</summary>
    string BuildAuthorizationUrl(string redirectUri, string state);

    Task<GoogleTokens> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default);

    Task<GoogleTokens> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Revokes the grant with Google. Failures are swallowed by callers: the local row is deleted regardless.</summary>
    Task RevokeAsync(string token, CancellationToken ct = default);
}
