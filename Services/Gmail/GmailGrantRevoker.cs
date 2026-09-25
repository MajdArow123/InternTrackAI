using InternTrackAI.Models;

namespace InternTrackAI.Services.Gmail;

/// <summary>
/// Revokes a stored Gmail grant with Google, best effort. The one place this is done, shared by
/// <c>IntegrationsController.Disconnect</c> and account deletion — deleting the local row alone leaves
/// InternTrackAI listed under the user's Google account as a third party with read access, which
/// account deletion used to do.
///
/// Never throws for a Google-side failure: the grant may already have been revoked from the Google
/// account page, or Google may be unreachable, and in both cases the caller removes the local copy
/// regardless. Capped at <see cref="Timeout"/> so a hung call to Google cannot hold an account
/// deletion open. A cancellation of the caller's own token still propagates.
/// </summary>
public class GmailGrantRevoker
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IGoogleOAuthClient _oauth;
    private readonly GmailTokenProtector _tokens;
    private readonly ILogger<GmailGrantRevoker> _logger;

    public GmailGrantRevoker(IGoogleOAuthClient oauth, GmailTokenProtector tokens, ILogger<GmailGrantRevoker> logger)
    {
        _oauth = oauth;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>Revokes the refresh token (which revokes the whole grant), else the access token. Returns whether Google accepted it.</summary>
    public async Task<bool> RevokeAsync(GmailConnection connection, CancellationToken ct = default)
    {
        var token = _tokens.Unprotect(connection.RefreshToken) ?? _tokens.Unprotect(connection.AccessToken);
        if (token is null) return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            await _oauth.RevokeAsync(token, cts.Token);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Gmail token revocation failed for user {UserId}; removing the connection anyway.", connection.UserId);
            return false;
        }
    }
}
