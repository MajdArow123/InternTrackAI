using System.Globalization;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>
/// Restores the shared demo account's <b>per-session state</b> — its profile fields, one pending resume
/// draft and one answered practice question (<see cref="RestoreAsync"/>), plus the inbox suggestions
/// when a session starts (<see cref="StartSessionAsync"/>) — so one visitor's session can't follow the
/// next one around.
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
    private readonly ResumeParseService _parser;
    private readonly ILogger<DemoProfileReset> _logger;

    public DemoProfileReset(ApplicationDbContext db, ResumeParseService parser, ILogger<DemoProfileReset> logger)
    {
        _db = db;
        _parser = parser;
        _logger = logger;
    }

    /// <summary>
    /// Puts the demo back to its <b>fixed starting state</b>: the profile's fields, one pending resume
    /// draft (the canned nursing parse) and one answered practice question. Returns false when there is
    /// no profile row to restore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A fixed state, not an empty one</b> (since 2026-09-24). This used to clear drafts and practice
    /// questions to nothing. It now replaces them with the same written rows for every visitor, because
    /// the guided tour's two most persuasive steps — a scored practice answer and the resume review
    /// screen — need something to point at, and the only other way to get one is a live model call in
    /// the middle of onboarding. Nobody's own work is shown to anybody else: every visitor gets exactly
    /// these rows, from <see cref="DemoSeeder.BuildAnsweredPracticeQuestion"/> and
    /// <see cref="ResumeParseService.StoreDemoParseAsync"/>.
    /// </para>
    /// <para>
    /// Whatever a previous visitor left is still removed first, for the reasons this class has always
    /// had: an unapplied stranger's draft would greet the next visitor, and a stranger's practice answers,
    /// averages and stars would fill <c>/Practice</c> — generated against whatever field that session
    /// had, so a software visitor could land on nursing questions.
    /// </para>
    /// <para>
    /// <see cref="DemoSeeder.ResetAsync"/> calls this too, so the nightly reseed and the per-session
    /// reset produce the same state by construction. <c>DemoResetTests</c> pins that they do.
    /// </para>
    /// </remarks>
    public async Task<bool> RestoreAsync(string userId, CancellationToken ct = default)
    {
        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null) return false;

        Apply(profile);

        var drafts = await _db.ParsedResumes.Where(p => p.UserId == userId).ToListAsync(ct);
        if (drafts.Count > 0) _db.ParsedResumes.RemoveRange(drafts);

        await _db.SaveChangesAsync(ct);

        // Separate from the save above: ExecuteDelete runs its own statement and does not participate
        // in the change tracker, so mixing the two in one SaveChanges would be misleading.
        await _db.PracticeQuestions.Where(q => q.UserId == userId).ExecuteDeleteAsync(ct);

        // Attached to the seeded interview it was written for, so /Practice shows it under that posting.
        // Null (general practice) if a visitor deleted that application; the nightly reseed restores it.
        var interviewId = await _db.JobApplications.AsNoTracking()
            .Where(a => a.UserId == userId && a.CompanyName == DemoSeeder.PracticeInterviewCompany)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(ct);
        _db.PracticeQuestions.Add(DemoSeeder.BuildAnsweredPracticeQuestion(userId, DateTime.UtcNow, interviewId));
        await _db.SaveChangesAsync(ct);

        // The pending draft is the real canned parse the demo's Analyze button produces — no model call,
        // no permit — tied to the active resume the way a real parse would be.
        var active = await _db.ResumeVersions.AsNoTracking()
            .Where(r => r.UserId == userId && r.IsActive)
            .Select(r => new { r.Id, r.ExtractedText })
            .FirstOrDefaultAsync(ct);
        await _parser.StoreDemoParseAsync(userId, active?.Id, active?.ExtractedText?.Length ?? 0, ct);

        return true;
    }

    /// <summary>
    /// Everything <see cref="RestoreAsync"/> does, plus the dashboard's three pending inbox suggestions
    /// and the three applications they point at. Run when a demo session starts
    /// (<c>POST /Account/DemoLogin</c>), never on a page view.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Accepting a suggestion moves its application (Shopify to Offer, say) and appends a "Status
    /// updated from email" note, so restoring the suggestion rows alone would offer to move Shopify to
    /// Offer when it is already there. The applications' <c>Status</c> and <c>InterviewAt</c> go back to
    /// <see cref="DemoSeeder.BuildApplications"/>'s values and the accept notes are removed.
    /// </para>
    /// <para>
    /// <b>Not part of <see cref="HealAsync"/></b>, which runs on a <c>/Profile</c> view: moving cards on a
    /// board a visitor may have open in another tab is exactly what that path promises not to do. A new
    /// session is the one moment it is safe.
    /// </para>
    /// </remarks>
    public async Task<bool> StartSessionAsync(string userId, CancellationToken ct = default)
    {
        if (!await RestoreAsync(userId, ct)) return false;

        var zoneId = await _db.UserProfiles.AsNoTracking()
            .Where(p => p.UserId == userId).Select(p => p.TimeZoneId).FirstOrDefaultAsync(ct);
        var seeded = DemoSeeder.BuildApplications(userId, UserClock.For(zoneId).Today);

        var apps = await _db.JobApplications
            .Where(a => a.UserId == userId && DemoSeeder.SuggestionCompanies.Contains(a.CompanyName))
            .ToListAsync(ct);

        foreach (var app in apps)
        {
            var original = seeded.FirstOrDefault(s => s.CompanyName == app.CompanyName && s.RoleTitle == app.RoleTitle);
            if (original is null) continue;          // a visitor renamed it; leave it for the nightly reseed
            app.Status      = original.Status;
            app.InterviewAt = original.InterviewAt;
        }

        // Filtered in memory with an ordinal comparison rather than a LIKE: SQLite's LIKE ignores case and
        // PostgreSQL's does not (CLAUDE.md §8), and this should remove exactly the notes Accept wrote.
        var appIds = apps.Select(a => a.Id).ToList();
        var acceptNotes = (await _db.ApplicationNotes
                .Where(n => n.UserId == userId && appIds.Contains(n.JobApplicationId))
                .ToListAsync(ct))
            .Where(n => n.Text.StartsWith(SuggestionService.NotePrefix, StringComparison.Ordinal))
            .ToList();
        _db.ApplicationNotes.RemoveRange(acceptNotes);

        _db.StatusSuggestions.RemoveRange(await _db.StatusSuggestions.Where(s => s.UserId == userId).ToListAsync(ct));
        _db.StatusSuggestions.AddRange(DemoSeeder.BuildSuggestions(userId, apps, DateTime.UtcNow));

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
