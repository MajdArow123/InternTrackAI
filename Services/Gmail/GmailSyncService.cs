using System.Text;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Services.Gmail;

/// <summary>Counts from one sync, for the log line and the "Sync now" toast. Never carries email content.</summary>
public sealed record GmailSyncResult(
    bool Connected,
    int MessagesFound,
    int Matched,
    int Classified,
    int Suggestions,
    int RateLimited,
    string? Error)
{
    public static GmailSyncResult NotConnected => new(false, 0, 0, 0, 0, 0, null);
    public static GmailSyncResult Failed(string error) => new(true, 0, 0, 0, 0, 0, error);

    public bool Success => Connected && Error is null;

    /// <summary>Toast text: "Checked 12 emails, 2 new suggestions."</summary>
    public string Message
    {
        get
        {
            if (!Connected) return "No Gmail account is connected.";
            if (Error is not null) return Error;
            var sb = new StringBuilder();
            sb.Append(MessagesFound == 0 ? "No new emails from your companies." : $"Checked {MessagesFound} email{(MessagesFound == 1 ? "" : "s")}");
            if (MessagesFound > 0) sb.Append(Suggestions == 0 ? ", no status changes found." : $", {Suggestions} new suggestion{(Suggestions == 1 ? "" : "s")}.");
            if (RateLimited > 0) sb.Append($" {RateLimited} left unclassified: AI limit reached, they will be picked up later.");
            return sb.ToString();
        }
    }
}

/// <summary>
/// One sync for one user: refresh the token if needed, search Gmail for mail from the companies
/// they have applied to, classify each candidate with the model, and store <see cref="StatusSuggestion"/>
/// rows. Rules the prompt fixed: 30-day look-back on the first run and <c>after:LastSyncedAt</c>
/// afterwards; at most <see cref="GmailOptions.MaxMessagesPerSync"/> messages; only headers, snippet
/// and the first text/plain part (cut to <see cref="GmailOptions.MaxBodyChars"/>) are read and the
/// body is dropped after classification; a Gmail message id is classified once per user; a
/// suggestion equal to the current status is dropped; every model call takes a permit from the
/// user's "ai" bucket. Logs counts only.
/// </summary>
public class GmailSyncService
{
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);
    public const string ReconnectMessage = "Google no longer accepts the saved Gmail access. Disconnect and connect again.";

    /// <summary>Words that make a company-name mention look like recruiting mail (used in the Gmail query and the local match).</summary>
    public static readonly string[] Keywords = { "interview", "offer", "application", "position", "role", "next steps", "unfortunately", "regret", "congratulations" };

    private static readonly Regex KeywordRegex = new(string.Join("|", Keywords.Select(k => @"\b" + Regex.Escape(k) + @"\b")), RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ApplicationDbContext _db;
    private readonly IGmailClient _gmail;
    private readonly IGoogleOAuthClient _oauth;
    private readonly IStatusClassifier _classifier;
    private readonly GmailTokenProtector _tokens;
    private readonly AiUsageLimiter _limiter;
    private readonly GmailOptions _options;
    private readonly IConfiguration _config;
    private readonly ILogger<GmailSyncService> _logger;
    private readonly TimeProvider _clock;

    public GmailSyncService(ApplicationDbContext db, IGmailClient gmail, IGoogleOAuthClient oauth, IStatusClassifier classifier,
                            GmailTokenProtector tokens, AiUsageLimiter limiter, IOptions<GmailOptions> options, IConfiguration config,
                            ILogger<GmailSyncService> logger, TimeProvider? clock = null)
    {
        _db = db;
        _gmail = gmail;
        _oauth = oauth;
        _classifier = classifier;
        _tokens = tokens;
        _limiter = limiter;
        _options = options.Value;
        _config = config;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<GmailSyncResult> SyncAsync(string userId, CancellationToken ct = default)
    {
        var connection = await _db.GmailConnections.FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (connection is null) return GmailSyncResult.NotConnected;

        var startedAt = _clock.GetUtcNow().UtcDateTime;

        // ── Token ────────────────────────────────────────
        var accessToken = await EnsureAccessTokenAsync(connection, startedAt, ct);
        if (accessToken is null) return GmailSyncResult.Failed(ReconnectMessage);

        // ── Candidate applications and their domains (in memory only) ──
        var apps = await _db.JobApplications.AsNoTracking()
            .Where(a => a.UserId == userId && (a.Status == ApplicationStatus.Applied || a.Status == ApplicationStatus.Interview || a.Status == ApplicationStatus.Offer))
            .ToListAsync(ct);
        if (apps.Count == 0)
        {
            await MarkSyncedAsync(connection, startedAt, ct);
            _logger.LogInformation("Gmail sync for user {UserId}: no active applications, nothing to search.", userId);
            return new GmailSyncResult(true, 0, 0, 0, 0, 0, null);
        }

        var appIds = apps.Select(a => a.Id).ToList();
        var notesByApp = (await _db.ApplicationNotes.AsNoTracking()
                .Where(n => n.UserId == userId && appIds.Contains(n.JobApplicationId))
                .Select(n => new { n.JobApplicationId, n.Text })
                .ToListAsync(ct))
            .GroupBy(n => n.JobApplicationId)
            .ToDictionary(g => g.Key, g => g.Select(n => n.Text).ToList());

        var candidates = apps.ToDictionary(a => a.Id, a => CompanyDomains.For(a.CompanyName, a.JobLink, notesByApp.GetValueOrDefault(a.Id)));

        // ── Gmail search ─────────────────────────────────
        var query = BuildQuery(apps, candidates, connection.LastSyncedAt);
        IReadOnlyList<string> ids;
        try
        {
            ids = await _gmail.ListMessageIdsAsync(accessToken, query, _options.MaxMessagesPerSync, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Gmail search failed for user {UserId}.", userId);
            return GmailSyncResult.Failed("Gmail could not be reached. Try again in a few minutes.");
        }
        ids = ids.Distinct().Take(_options.MaxMessagesPerSync).ToList();

        var seen = (await _db.StatusSuggestions.Where(s => s.UserId == userId && ids.Contains(s.GmailMessageId)).Select(s => s.GmailMessageId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var pendingByApp = (await _db.StatusSuggestions.Where(s => s.UserId == userId && s.Status == SuggestionState.Pending && appIds.Contains(s.ApplicationId))
                .Select(s => new { s.ApplicationId, s.SuggestedStatus }).ToListAsync(ct))
            .Select(s => (s.ApplicationId, s.SuggestedStatus)).ToHashSet();

        var isDemo  = AiRateLimiting.IsDemoEmail(await _db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct), _config);
        var profile = await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId).Select(p => new { p.TimeZoneId }).FirstOrDefaultAsync(ct);
        var userClock = UserClock.For(profile?.TimeZoneId, startedAt);

        int matched = 0, classified = 0, added = 0, rateLimited = 0;
        var newSuggestions = new List<StatusSuggestion>();

        foreach (var id in ids)
        {
            if (seen.Contains(id)) continue;

            GmailMessage? message;
            try { message = await _gmail.GetMessageAsync(accessToken, id, _options.MaxBodyChars, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Gmail message fetch failed for user {UserId}; skipping one message.", userId);
                continue;
            }
            if (message is null) continue;

            var app = MatchApplication(message, apps, candidates);
            if (app is null) continue;
            matched++;

            using var lease = _limiter.TryAcquire(userId, isDemo);
            if (!lease.IsAcquired)
            {
                rateLimited++;
                continue;   // not marked seen: the next sync retries it
            }

            var result = await _classifier.ClassifyAsync(new ClassifierInput(
                app.CompanyName, app.RoleTitle, app.Status, message.Subject, message.From,
                message.Body.Length > _options.MaxBodyChars ? message.Body[.._options.MaxBodyChars] : message.Body,
                userClock.Today, userClock.ZoneId), ct);
            classified++;

            if (result is null || !result.Matches || result.SuggestedStatus is null) continue;
            if (result.SuggestedStatus == app.Status) continue;                          // nothing new
            if (!pendingByApp.Add((app.Id, result.SuggestedStatus.Value))) continue;    // same proposal already waiting

            newSuggestions.Add(new StatusSuggestion
            {
                ApplicationId   = app.Id,
                UserId          = userId,
                GmailMessageId  = message.Id,
                SuggestedStatus = result.SuggestedStatus.Value,
                Confidence      = result.Confidence,
                Summary         = string.IsNullOrWhiteSpace(result.Summary) ? DefaultSummary(result.SuggestedStatus.Value, app.CompanyName) : result.Summary,
                InterviewAt     = result.SuggestedStatus == ApplicationStatus.Interview ? ToUtc(result.InterviewAt, userClock) : null,
                EmailSubject    = Cut(message.Subject, 300),
                EmailFrom       = Cut(message.From, 320),
                EmailDate       = message.DateUtc,
                Status          = SuggestionState.Pending,
                CreatedAt       = startedAt
            });
            seen.Add(message.Id);
            added++;
        }

        _db.StatusSuggestions.AddRange(newSuggestions);
        if (rateLimited == 0)
            await MarkSyncedAsync(connection, startedAt, ct);   // otherwise the window stays open so the skipped mail is searched again
        else
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Gmail sync for user {UserId}: {Found} messages, {Matched} matched, {Classified} classified, {Added} suggestions, {RateLimited} rate-limited.",
            userId, ids.Count, matched, classified, added, rateLimited);

        return new GmailSyncResult(true, ids.Count, matched, classified, added, rateLimited, null);
    }

    // ── Pieces (public + static where the tests want them) ──────────────────

    /// <summary>
    /// The Gmail search: time window + (sender domains OR company name with a recruiting keyword).
    /// First sync: <c>newer_than:30d</c>; later: <c>after:{LastSyncedAt as epoch seconds}</c>.
    /// </summary>
    public static string BuildQuery(IEnumerable<JobApplication> apps, IReadOnlyDictionary<int, HashSet<string>> candidates, DateTime? lastSyncedAt)
    {
        var window = lastSyncedAt.HasValue
            ? "after:" + new DateTimeOffset(DateTime.SpecifyKind(lastSyncedAt.Value, DateTimeKind.Utc)).ToUnixTimeSeconds()
            : "newer_than:30d";

        var domains = candidates.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d).ToList();
        var keywordClause = "(" + string.Join(" OR ", Keywords.Select(k => k.Contains(' ') ? $"\"{k}\"" : k)) + ")";
        var companies = apps.Select(a => a.CompanyName.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

        var clauses = new List<string>();
        if (domains.Count > 0) clauses.Add("(" + string.Join(" OR ", domains.Select(d => "from:" + d)) + ")");
        foreach (var c in companies) clauses.Add($"(\"{c.Replace("\"", "")}\" {keywordClause})");

        return window + " (" + string.Join(" OR ", clauses) + ")";
    }

    /// <summary>
    /// Which application an email belongs to: the sender's domain first, then a company-name mention
    /// together with a recruiting keyword. Several candidates → the one whose role title appears in
    /// the email, else the most recently created.
    /// </summary>
    public static JobApplication? MatchApplication(GmailMessage message, IReadOnlyList<JobApplication> apps, IReadOnlyDictionary<int, HashSet<string>> candidates)
    {
        var senderDomain = GmailApiClient.SenderDomain(message.From);
        var text = string.Join('\n', message.Subject, message.Snippet, message.Body);

        var byDomain = apps.Where(a => candidates.TryGetValue(a.Id, out var set) && CompanyDomains.Matches(senderDomain, set)).ToList();
        var pool = byDomain.Count > 0
            ? byDomain
            : KeywordRegex.IsMatch(text)
                ? apps.Where(a => a.CompanyName.Length >= 2 && text.Contains(a.CompanyName, StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<JobApplication>();

        if (pool.Count == 0) return null;
        if (pool.Count == 1) return pool[0];

        var byRole = pool.Where(a => !string.IsNullOrWhiteSpace(a.RoleTitle) && text.Contains(a.RoleTitle, StringComparison.OrdinalIgnoreCase)).ToList();
        return (byRole.Count > 0 ? byRole : pool).OrderByDescending(a => a.Id).First();
    }

    /// <summary>Unspecified = wall-clock in the user's zone; Utc stays as is.</summary>
    public static DateTime? ToUtc(DateTime? value, UserClock clock)
    {
        if (!value.HasValue) return null;
        return value.Value.Kind == DateTimeKind.Utc ? value : clock.ToUtc(value.Value);
    }

    public static string DefaultSummary(ApplicationStatus status, string company) => status switch
    {
        ApplicationStatus.Interview => $"{company} wants to schedule an interview.",
        ApplicationStatus.Offer     => $"{company} sent an offer.",
        ApplicationStatus.Rejected  => $"{company} declined the application.",
        _                           => $"Email from {company}."
    };

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private async Task<string?> EnsureAccessTokenAsync(GmailConnection connection, DateTime nowUtc, CancellationToken ct)
    {
        var access  = _tokens.Unprotect(connection.AccessToken);
        var refresh = _tokens.Unprotect(connection.RefreshToken);
        if (refresh is null) return null;

        if (access is not null && connection.TokenExpiresAt > nowUtc.Add(RefreshWindow)) return access;

        try
        {
            var fresh = await _oauth.RefreshAsync(refresh, ct);
            connection.AccessToken    = _tokens.Protect(fresh.AccessToken);
            connection.TokenExpiresAt = fresh.ExpiresAtUtc;
            if (!string.IsNullOrEmpty(fresh.RefreshToken)) connection.RefreshToken = _tokens.Protect(fresh.RefreshToken);
            await _db.SaveChangesAsync(ct);
            return fresh.AccessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Gmail token refresh failed for user {UserId}.", connection.UserId);
            return null;
        }
    }

    private async Task MarkSyncedAsync(GmailConnection connection, DateTime startedAt, CancellationToken ct)
    {
        connection.LastSyncedAt = startedAt;
        await _db.SaveChangesAsync(ct);
    }
}
