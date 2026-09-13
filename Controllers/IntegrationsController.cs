using System.Security.Claims;
using System.Security.Cryptography;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Controllers;

/// <summary>
/// Gmail connect / disconnect (OAuth 2.0 authorization-code flow, <c>gmail.readonly</c> only).
/// Every action is owner-scoped: a user can only ever see, create or remove their own
/// <see cref="GmailConnection"/>. When <c>Google:ClientId</c> / <c>Google:ClientSecret</c> are not
/// configured all actions answer 404, matching the hidden UI.
///
/// CSRF protection for the redirect round-trip: Connect stores a data-protected
/// <c>{userId, nonce, expiry}</c> in a short-lived cookie and sends the nonce to Google as
/// <c>state</c>; Callback accepts the code only if the cookie unprotects, belongs to the signed-in
/// user, has not expired and its nonce equals the returned <c>state</c>.
/// </summary>
[Authorize]
[Route("Integrations/Gmail")]
public class IntegrationsController : Controller
{
    public const string StateCookie = "itai_gmail_state";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;
    private readonly IGoogleOAuthClient _oauth;
    private readonly IGmailClient _gmail;
    private readonly GmailSyncService _sync;
    private readonly GmailTokenProtector _tokens;
    private readonly IDataProtector _stateProtector;
    private readonly ILogger<IntegrationsController> _logger;

    public IntegrationsController(ApplicationDbContext db, IConfiguration config, IGoogleOAuthClient oauth, IGmailClient gmail, GmailSyncService sync,
                                  GmailTokenProtector tokens, IDataProtectionProvider dataProtection, ILogger<IntegrationsController> logger)
    {
        _db = db;
        _config = config;
        _oauth = oauth;
        _gmail = gmail;
        _sync = sync;
        _tokens = tokens;
        _stateProtector = dataProtection.CreateProtector("InternTrackAI.GmailOAuthState.v1");
        _logger = logger;
    }

    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private bool Enabled => GmailIntegration.IsConfigured(_config);

    /// <summary>Absolute redirect URI registered with Google: <c>{scheme}://{host}/Integrations/Gmail/Callback</c>.</summary>
    private string RedirectUri() => Url.Action(nameof(Callback), "Integrations", null, Request.Scheme, Request.Host.ToUriComponent())!;

    private IActionResult BackToProfile(string toastType, string message)
    {
        TempData["Toast"] = $"{toastType}|{message}";
        return RedirectToAction("Index", "Profile");
    }

    // ── GET /Integrations/Gmail/Connect ──────────────────

    /// <summary>Starts the Google consent flow (gmail.readonly, offline access, forced consent) with a user-bound state.</summary>
    [HttpGet("Connect")]
    public IActionResult Connect()
    {
        if (!Enabled) return NotFound();

        var nonce   = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = DateTimeOffset.UtcNow.Add(StateLifetime);
        var payload = _stateProtector.Protect($"{UserId()}|{nonce}|{expires.ToUnixTimeSeconds()}");

        Response.Cookies.Append(StateCookie, payload, new CookieOptions
        {
            HttpOnly = true,
            Secure   = Request.IsHttps,
            SameSite = SameSiteMode.Lax,   // Lax: the cookie must ride along on Google's top-level redirect back to /Callback
            Expires  = expires,
            IsEssential = true,
            Path     = "/Integrations/Gmail"
        });

        return Redirect(_oauth.BuildAuthorizationUrl(RedirectUri(), nonce));
    }

    // ── GET /Integrations/Gmail/Callback ─────────────────

    /// <summary>Validates state, exchanges the code, reads the account address and stores the (encrypted) tokens.</summary>
    [HttpGet("Callback")]
    public async Task<IActionResult> Callback(string? code, string? state, string? error)
    {
        if (!Enabled) return NotFound();

        var userId = UserId();
        var stateOk = ValidateState(userId, state);
        Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/Integrations/Gmail" });

        if (!stateOk)
        {
            _logger.LogWarning("Gmail OAuth callback rejected for user {UserId}: state mismatch or expired.", userId);
            return BackToProfile("error", "Gmail connection could not be verified. Please try again.");
        }

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            _logger.LogInformation("Gmail OAuth callback without a code for user {UserId} ({Error}).", userId, error ?? "no code");
            return BackToProfile("info", "Gmail connection was cancelled.");
        }

        GoogleTokens tokens;
        string? address;
        try
        {
            tokens  = await _oauth.ExchangeCodeAsync(code, RedirectUri(), HttpContext.RequestAborted);
            address = await _gmail.GetProfileEmailAsync(tokens.AccessToken, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Gmail OAuth code exchange failed for user {UserId}.", userId);
            return BackToProfile("error", "Google did not accept the connection. Please try again.");
        }

        if (string.IsNullOrWhiteSpace(address))
            return BackToProfile("error", "Google did not return the Gmail address for that account.");

        var connection = await _db.GmailConnections.FirstOrDefaultAsync(c => c.UserId == userId);
        if (connection is null)
        {
            connection = new GmailConnection { UserId = userId, CreatedAt = DateTime.UtcNow };
            _db.GmailConnections.Add(connection);
        }

        connection.GmailAddress   = address.Trim();
        connection.AccessToken    = _tokens.Protect(tokens.AccessToken);
        connection.TokenExpiresAt = tokens.ExpiresAtUtc;
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
            connection.RefreshToken = _tokens.Protect(tokens.RefreshToken);   // prompt=consent always returns one; keep the old one if not
        else if (string.IsNullOrEmpty(connection.RefreshToken))
        {
            _logger.LogWarning("Gmail OAuth returned no refresh token for user {UserId}; connection not stored.", userId);
            return BackToProfile("error", "Google did not grant offline access. Please try connecting again.");
        }
        // Reconnecting starts over: the next sync looks back 30 days again.
        connection.LastSyncedAt  = null;
        connection.LastHistoryId = null;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Gmail connected for user {UserId}.", userId);
        return BackToProfile("success", $"Gmail connected: {connection.GmailAddress}.");
    }

    // ── POST /Integrations/Gmail/Sync ────────────────────

    /// <summary>"Sync now": runs one sync for the signed-in user and reports the counts as a toast.</summary>
    [HttpPost("Sync"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync()
    {
        if (!Enabled) return NotFound();

        var result = await _sync.SyncAsync(UserId(), HttpContext.RequestAborted);
        var type   = !result.Connected ? "info" : result.Error is not null ? "error" : result.Suggestions > 0 ? "success" : "info";
        return BackToProfile(type, result.Message);
    }

    // ── POST /Integrations/Gmail/Disconnect ──────────────

    /// <summary>Revokes the grant with Google (best effort) and deletes the user's connection and pending suggestions.</summary>
    [HttpPost("Disconnect"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect()
    {
        if (!Enabled) return NotFound();

        var userId     = UserId();
        var connection = await _db.GmailConnections.FirstOrDefaultAsync(c => c.UserId == userId);
        if (connection is null)
            return BackToProfile("info", "No Gmail account is connected.");

        var token = _tokens.Unprotect(connection.RefreshToken) ?? _tokens.Unprotect(connection.AccessToken);
        if (token is not null)
        {
            try { await _oauth.RevokeAsync(token, HttpContext.RequestAborted); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Already revoked from the Google account page, or Google unreachable: the local copy goes either way.
                _logger.LogWarning(ex, "Gmail token revocation failed for user {UserId}; removing the connection anyway.", userId);
            }
        }

        // Pending proposals from that inbox go too; accepted/dismissed ones stay as history on the application.
        await _db.StatusSuggestions.Where(s => s.UserId == userId && s.Status == Models.Enums.SuggestionState.Pending).ExecuteDeleteAsync();
        _db.GmailConnections.Remove(connection);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Gmail disconnected for user {UserId}.", userId);
        return BackToProfile("success", "Gmail disconnected. InternTrackAI no longer has access to that inbox.");
    }

    /// <summary>The cookie must unprotect, name this user, carry the returned nonce and not be expired.</summary>
    private bool ValidateState(string userId, string? state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        if (!Request.Cookies.TryGetValue(StateCookie, out var cookie) || string.IsNullOrEmpty(cookie)) return false;

        string plain;
        try { plain = _stateProtector.Unprotect(cookie); }
        catch (CryptographicException) { return false; }

        var parts = plain.Split('|');
        if (parts.Length != 3) return false;
        if (!string.Equals(parts[0], userId, StringComparison.Ordinal)) return false;
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(parts[1]), System.Text.Encoding.UTF8.GetBytes(state))) return false;
        return long.TryParse(parts[2], out var exp) && DateTimeOffset.FromUnixTimeSeconds(exp) > DateTimeOffset.UtcNow;
    }
}
