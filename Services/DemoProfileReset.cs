using System.Globalization;
using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>
/// Restores the shared demo account's <b>profile fields only</b>, so one visitor's resume review
/// can't follow the next one around.
/// </summary>
/// <remarks>
/// <para>
/// The demo's review screen shows a nursing parse on purpose (see
/// <see cref="ProfileExtractorService.DemoParse"/>) — it is how a visitor sees in one screen that
/// the app is not IT-only. But applying it writes clinical skills, roles and a Healthcare field onto
/// a shared profile whose 15 seeded applications are all software. <see cref="DemoSeeder"/> puts that
/// right every night, which left a window of up to 24 hours where the demo read as a nurse applying
/// to Stripe. This closes it to a single browser session.
/// </para>
/// <para>
/// <b>Narrow on purpose.</b> This is not a <see cref="DemoSeeder.ResetAsync"/>: it touches the
/// profile's own fields and the resume-parse drafts, and nothing else. Applications, notes, cover
/// letters, prep sessions and resume versions are left exactly as they are, because a visitor
/// halfway through looking at the board must not have it rebuilt underneath them — and because a
/// full reseed on every demo sign-in would be a lot of writes for a page view.
/// </para>
/// <para>
/// <b>Who gets reset.</b> Two triggers, both cheap: signing in through
/// <c>POST /Account/DemoLogin</c>, so a new session always starts clean; and opening
/// <c>/Profile</c> when the profile was last enriched by <em>a different browser</em>. The session
/// that applied the review keeps its result — seeing the merge land is the payoff of the flow, and
/// a button that visibly does nothing would be a worse demo than a slightly odd profile.
/// </para>
/// </remarks>
public class DemoProfileReset
{
    /// <summary>
    /// Marks the browser that applied the review, so <see cref="HealAsync"/> can tell "I did this"
    /// from "a stranger did this". A session cookie: it should die with the browser, exactly as the
    /// demo session does.
    /// </summary>
    public const string CookieName = "itai_demo_review";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<DemoProfileReset> _logger;

    public DemoProfileReset(ApplicationDbContext db, ILogger<DemoProfileReset> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Puts the demo profile's fields back to the <see cref="DemoSeeder"/> constants and drops any
    /// resume-parse drafts. Returns false when there is no profile row to restore.
    /// </summary>
    /// <remarks>
    /// Drafts go too: an unapplied draft from a stranger would greet the next visitor with a
    /// "Resume analysis waiting for you" card for a review they never ran, which is a confusing
    /// first impression of the one screen this demo exists to show off.
    /// </remarks>
    public async Task<bool> RestoreAsync(string userId, CancellationToken ct = default)
    {
        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null) return false;

        Apply(profile);

        var drafts = await _db.ParsedResumes.Where(p => p.UserId == userId).ToListAsync(ct);
        if (drafts.Count > 0) _db.ParsedResumes.RemoveRange(drafts);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Resets the profile when it was last enriched by a browser other than this one. Returns true
    /// if it reset, so the caller can log or re-read.
    /// </summary>
    /// <param name="cookieStamp">The <see cref="CookieName"/> value from the request, or null.</param>
    public async Task<bool> HealAsync(string userId, string? cookieStamp, CancellationToken ct = default)
    {
        var enrichedAt = await _db.UserProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.ProfileLastEnrichedAt)
            .FirstOrDefaultAsync(ct);

        // Nobody has applied a review since the last reset: nothing to undo.
        if (enrichedAt is not { } enriched) return false;

        // This browser is the one that applied it — leave its result alone.
        if (cookieStamp is not null && cookieStamp == Stamp(enriched)) return false;

        var reset = await RestoreAsync(userId, ct);
        if (reset)
            _logger.LogInformation("Demo profile fields restored: a resume review applied at {EnrichedAt} came from another session.", enriched);

        return reset;
    }

    /// <summary>Applies the demo's fixed field values to a loaded profile, without saving.</summary>
    /// <remarks>
    /// The single definition of "what the demo profile's fields are" —
    /// <see cref="DemoSeeder.EnsureProfileAsync"/> calls this too, so the nightly reseed and the
    /// per-session reset can never drift apart.
    /// </remarks>
    public static void Apply(UserProfile profile)
    {
        profile.TargetRolesJson = ProfileTags.ToJson(DemoSeeder.TargetRoles);
        profile.SkillsJson      = ProfileTags.ToJson(DemoSeeder.Skills);
        profile.DisplayName     = DemoSeeder.DisplayName;
        profile.FullName        = DemoSeeder.FullName;
        profile.Country         = DemoSeeder.Country;
        profile.Field           = DemoSeeder.Field;
        profile.FieldCategory   = DemoSeeder.Category;
        profile.Seniority       = DemoSeeder.Seniority;
        profile.YearsExperience = DemoSeeder.YearsExperience;
        profile.Location        = DemoSeeder.Location;

        // Cleared last: it is the signal HealAsync reads, so leaving it set would make the very next
        // request think another session had just applied something.
        profile.ProfileLastEnrichedAt = null;
    }

    /// <summary>
    /// The cookie value for an enrichment time, truncated to whole seconds.
    /// </summary>
    /// <remarks>
    /// Truncated because the stamp is written from one database round-trip and compared against
    /// another: SQLite keeps sub-millisecond precision and PostgreSQL's <c>timestamptz</c> rounds to
    /// microseconds, so raw ticks can come back different from what went in and the applying visitor
    /// would reset their own result. A second is coarse enough to survive that and fine enough that
    /// a collision needs two applies in the same second — where the cost is one missed reset.
    /// </remarks>
    public static string Stamp(DateTime enrichedAt) =>
        enrichedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>Reads this browser's marker, if it has one.</summary>
    public static string? ReadStamp(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieName, out var value) ? value : null;

    /// <summary>Marks this browser as the one that applied the review.</summary>
    public static void Remember(HttpResponse response, DateTime enrichedAt) =>
        response.Cookies.Append(CookieName, Stamp(enrichedAt), new CookieOptions
        {
            HttpOnly    = true,
            Secure      = response.HttpContext.Request.IsHttps,
            SameSite    = SameSiteMode.Lax,
            IsEssential = true
            // No Expires: a session cookie, so closing the browser ends the exemption too.
        });

    /// <summary>Drops the marker, so the next <c>/Profile</c> view is treated as a fresh visitor's.</summary>
    public static void Forget(HttpResponse response) => response.Cookies.Delete(CookieName);
}
