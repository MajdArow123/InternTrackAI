namespace InternTrackAI.Services.Gmail;

/// <summary>
/// The parts of one Gmail message the sync looks at. <see cref="Body"/> is the first text/plain
/// part, already cut to the configured maximum; it lives only for the duration of the sync and is
/// never written anywhere (the StatusSuggestions row keeps subject, sender, date and the AI summary).
/// </summary>
public sealed record GmailMessage(
    string Id,
    string Subject,
    string From,
    DateTime? DateUtc,
    string Snippet,
    string Body);

/// <summary>
/// Read-only Gmail access used by the OAuth callback (account address) and the sync (search + read).
/// <see cref="GmailApiClient"/> is the Google.Apis.Gmail.v1 implementation; tests register a fake.
/// </summary>
public interface IGmailClient
{
    /// <summary>The address of the account the token belongs to (users.getProfile).</summary>
    Task<string?> GetProfileEmailAsync(string accessToken, CancellationToken ct = default);

    /// <summary>Ids of the messages matching a Gmail search query, newest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<string>> ListMessageIdsAsync(string accessToken, string query, int max, CancellationToken ct = default);

    /// <summary>Headers, snippet and the first text/plain part (cut to <paramref name="maxBodyChars"/>) of one message; null if it is gone.</summary>
    Task<GmailMessage?> GetMessageAsync(string accessToken, string id, int maxBodyChars, CancellationToken ct = default);
}
