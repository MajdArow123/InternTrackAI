using System.ComponentModel.DataAnnotations;

namespace InternTrackAI.Models;

/// <summary>
/// One user's linked Gmail account (at most one per user, enforced by a unique index on
/// <see cref="UserId"/>). Created by the OAuth callback in <c>IntegrationsController</c>, read by
/// <c>GmailSyncService</c>, deleted (after revoking the token with Google) by Disconnect.
///
/// <see cref="AccessToken"/> and <see cref="RefreshToken"/> hold <b>ciphertext</b>, never the raw
/// Google tokens: every write goes through <c>GmailTokenProtector.Protect</c> (ASP.NET Data
/// Protection, keys persisted in the DataProtectionKeys table) and every read through
/// <c>Unprotect</c>. Nothing in this row identifies an email beyond the account address.
/// </summary>
public class GmailConnection
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The Gmail address Google reported for the granted account (users.getProfile).</summary>
    [Required, StringLength(320)]
    public string GmailAddress { get; set; } = string.Empty;

    /// <summary>Data-protected OAuth access token (short-lived; refreshed by the sync when within 5 minutes of expiry).</summary>
    [Required]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Data-protected OAuth refresh token (granted because Connect asks for access_type=offline).</summary>
    [Required]
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>UTC instant the current access token stops working.</summary>
    public DateTime TokenExpiresAt { get; set; }

    /// <summary>UTC instant of the last successful sync; null until the first one. Drives the <c>after:</c> Gmail query.</summary>
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>Gmail history id seen on the last sync. Recorded for a future incremental (history.list) sync; not used yet.</summary>
    [StringLength(40)]
    public string? LastHistoryId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
