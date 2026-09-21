using System.Security.Claims;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Helpers;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;
using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Controllers;

/// <summary>
/// Manages the user's career portfolio: personal info, profile photo, skills/target-role tags,
/// resume version history (upload, activate, delete, download), the resume → profile parse and its
/// review screen, AI resume scoring, the AI resume-match endpoint used by the Job Application create
/// page, and the "Rewrite a bullet" tool.
///
/// <para>
/// <b>No action here writes AI output to the profile except <see cref="ApplyResumeReview"/></b>, and
/// that one runs only on a draft the user has confirmed on <see cref="ReviewResume"/>. Upload and
/// re-parse both end at that screen. Adding a second path that merges directly re-creates the silent
/// overwrite this flow was built to remove.
/// </para>
/// </summary>
[Authorize]
public class ProfileController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly UploadStorage _uploads;
    private readonly ResumeScoreService _scorer;
    private readonly ResumeMatcherService _matcher;
    private readonly ResumeRewriteService _rewriter;
    private readonly ProfileAutoFillService _autoFill;
    private readonly ResumeParseService _parser;
    private readonly DemoProfileReset _demoProfile;
    private readonly ResumeTextService _resumeText;
    private readonly IUserContextBuilder _userContext;
    private readonly TargetRoleSeeds _roleSeeds;
    private readonly AiUsageLimiter _aiLimiter;
    private readonly GitHubService _github;
    private readonly UserClockProvider _clocks;
    private readonly IConfiguration _config;
    private readonly ILogger<ProfileController> _logger;

    public ProfileController(
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager,
        UploadStorage uploads,
        ResumeScoreService scorer,
        ResumeMatcherService matcher,
        ResumeRewriteService rewriter,
        ProfileAutoFillService autoFill,
        ResumeParseService parser,
        DemoProfileReset demoProfile,
        ResumeTextService resumeText,
        IUserContextBuilder userContext,
        TargetRoleSeeds roleSeeds,
        AiUsageLimiter aiLimiter,
        GitHubService github,
        UserClockProvider clocks,
        IConfiguration config,
        ILogger<ProfileController> logger)
    {
        _clocks = clocks;
        _config = config;
        _db = db;
        _userManager = userManager;
        _uploads = uploads;
        _scorer = scorer;
        _matcher = matcher;
        _rewriter = rewriter;
        _autoFill = autoFill;
        _parser = parser;
        _demoProfile = demoProfile;
        _resumeText = resumeText;
        _userContext = userContext;
        _roleSeeds = roleSeeds;
        _aiLimiter = aiLimiter;
        _github = github;
        _logger = logger;
    }

    // ── GET /Profile ────────────────────────────────────

    /// <summary>Renders the full profile page: personal info, documents, skills, and application stats.</summary>
    /// <remarks>
    /// Writes on a GET in two cases, both idempotent and both long-standing patterns here: issuing the
    /// calendar token on first view, and — on the shared demo account only — undoing a resume review
    /// some other visitor applied (see <see cref="DemoProfileReset"/>). Neither changes what the page
    /// shows a second time.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var userId = UserId();

        if (AiRateLimiting.IsDemoUser(User, _config))
            await _demoProfile.HealAsync(userId, DemoProfileReset.ReadStamp(Request), HttpContext.RequestAborted);

        await EnsureCalendarTokenAsync(userId);
        var vm = await BuildViewModelAsync();
        return View(vm);
    }

    // ── GET /Profile/Bookmarklet ─────────────────────────

    /// <summary>
    /// Install page for the "Save to InternTrackAI" bookmarklet. The bookmark code is generated
    /// from this request's scheme and host (see <see cref="BookmarkletViewModel"/>) so it targets
    /// the origin the user is actually on; behind Railway's proxy the forwarded-headers middleware
    /// has already restored the public https scheme by the time this runs.
    /// </summary>
    [HttpGet]
    public IActionResult Bookmarklet()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host.ToUriComponent()}{Request.PathBase.ToUriComponent()}";
        return View(new BookmarkletViewModel { BaseUrl = baseUrl });
    }

    // ── POST /Profile/SaveInfo ───────────────────────────

    /// <summary>
    /// Saves the personal-info section of the profile via AJAX (creates the profile row if it
    /// doesn't exist yet). Returns JSON rather than redirecting so the page stays at the user's
    /// current scroll position and can show a toast instead of a full reload.
    /// </summary>
    /// <returns>JSON <c>{ success, error }</c> — <c>error</c> is set if <paramref name="fullName"/> is blank or <paramref name="timeZoneId"/> is not a zone this host knows.</returns>
    /// <remarks>
    /// The field-awareness values (<paramref name="field"/>, <paramref name="fieldCategory"/>,
    /// <paramref name="seniority"/>, <paramref name="yearsExperience"/>, <paramref name="location"/>)
    /// are all optional and all go through <see cref="ProfileFields"/>, which turns anything
    /// unrecognised into null or <c>Other</c> rather than failing the save — a stale client that omits
    /// them entirely posts nulls and simply clears them, same as clearing any other optional field.
    /// </remarks>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveInfo(string? fullName, string? displayName, string? country, string? phoneNumber, string? githubUsername, string? timeZoneId,
        string? field = null, string? fieldCategory = null, string? seniority = null, int? yearsExperience = null, string? location = null)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return Json(new { success = false, error = "Full name is required." });
        if (timeZoneId is not null && !TimeZones.IsValid(timeZoneId))
            return Json(new { success = false, error = "Unknown time zone.", field = "timeZoneId" });

        var userId = UserId();
        var profile = await GetOrCreateProfileAsync(userId);

        // The shared demo account's display name is fixed (DemoSeeder restores it nightly). Its input is disabled, so
        // FormData omits it: an absent value keeps the stored name, and a posted change is refused without saving anything.
        var newDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (AiRateLimiting.IsDemoUser(User, _config))
        {
            if (displayName is not null && newDisplayName != profile.DisplayName)
                return Json(new { success = false, error = ConfiguredAccounts.DemoUnavailableMessage, field = "displayName" });
            newDisplayName = profile.DisplayName;
        }

        profile.FullName       = fullName.Trim();
        profile.DisplayName    = newDisplayName;
        profile.Country        = country?.Trim();
        profile.PhoneNumber    = phoneNumber?.Trim();
        profile.GitHubUsername = string.IsNullOrWhiteSpace(githubUsername) ? null : githubUsername.Trim().TrimStart('@');
        if (timeZoneId is not null) profile.TimeZoneId = timeZoneId;   // older clients without the dropdown leave it unchanged

        profile.Field           = ProfileFields.Text(field);
        profile.FieldCategory   = ProfileFields.ParseCategory(fieldCategory);
        profile.Seniority       = ProfileFields.ParseSeniority(seniority);
        profile.YearsExperience = ProfileFields.Years(yearsExperience);
        profile.Location        = ProfileFields.Text(location);

        await _db.SaveChangesAsync();
        var clock = UserClock.For(profile.TimeZoneId);   // fresh, not the request-cached clock: the zone may have just changed
        // fieldCategory comes back so the role-suggestion combobox can re-point at the saved value
        // (ProfileFields may have coerced an unrecognised one to Other).
        return Json(new
        {
            success = true,
            timeZoneId = profile.TimeZoneId,
            nowLocal = clock.LocalTime(clock.NowUtc),
            fieldCategory = profile.FieldCategory?.ToString()
        });
    }

    // ── POST /Profile/RegenerateCalendarToken ────────────

    /// <summary>Rotates the calendar-feed token; the previous feed URL stops working immediately.</summary>
    /// <returns>JSON <c>{ success, url }</c>.</returns>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateCalendarToken()
    {
        var profile = await GetOrCreateProfileAsync(UserId());
        profile.CalendarToken = CalendarController.NewToken();
        await _db.SaveChangesAsync();
        return Json(new { success = true, url = CalendarFeedUrl(profile.CalendarToken) });
    }

    /// <summary>Issues the feed token the first time the profile page is opened.</summary>
    private async Task EnsureCalendarTokenAsync(string userId)
    {
        var profile = await GetOrCreateProfileAsync(userId);
        if (string.IsNullOrEmpty(profile.CalendarToken))
        {
            profile.CalendarToken = CalendarController.NewToken();
            await _db.SaveChangesAsync();
        }
    }

    private string? CalendarFeedUrl(string? token) =>
        string.IsNullOrEmpty(token) ? null : Url.Action("Feed", "Calendar", new { token }, Request.Scheme);

    // ── POST /Profile/SaveReminderSettings ───────────────

    /// <summary>Saves the follow-up window (3–30 days) used by <see cref="ReminderService"/>.</summary>
    /// <returns>JSON <c>{ success, followUpAfterDays }</c> or <c>{ success:false, error }</c>.</returns>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveReminderSettings(int? followUpAfterDays)
    {
        if (followUpAfterDays is null
            || followUpAfterDays < ReminderService.MinFollowUpAfterDays
            || followUpAfterDays > ReminderService.MaxFollowUpAfterDays)
        {
            return Json(new { success = false, error = $"Choose between {ReminderService.MinFollowUpAfterDays} and {ReminderService.MaxFollowUpAfterDays} days." });
        }

        var profile = await GetOrCreateProfileAsync(UserId());
        profile.FollowUpAfterDays = followUpAfterDays.Value;
        await _db.SaveChangesAsync();
        return Json(new { success = true, followUpAfterDays = profile.FollowUpAfterDays });
    }

    // ── POST /Profile/UploadPhoto ────────────────────────

    public const int MaxPhotoBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Replaces the user's profile photo. Validates extension and size, deletes the previous
    /// photo file, and bumps <c>PhotoVersion</c> so the new image cache-busts in the UI (the
    /// stored filename is the user id, so the version number is the only thing that changes).
    /// The avatar on the profile page posts this via fetch and gets JSON <c>{ success, url, error }</c>;
    /// a plain form post (no JS) still gets the redirect-with-toast behaviour.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadPhoto(IFormFile? photo)
    {
        if (photo == null || photo.Length == 0)
            return PhotoError("Please select a photo.");

        var ext = Path.GetExtension(photo.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            return PhotoError("Photos must be JPG, PNG, or WebP.");

        if (photo.Length > MaxPhotoBytes)
            return PhotoError("Photo must be under 2 MB.");

        var userId = UserId();
        var dir = _uploads.PhotosDirectory;

        var profile = await GetOrCreateProfileAsync(userId);
        DeletePhotoFile(profile);

        var fileName = $"{userId}{ext}";
        await using (var fs = System.IO.File.Create(Path.Combine(dir, fileName)))
        {
            await photo.CopyToAsync(fs);
        }

        profile.PhotoFileName = fileName;
        profile.PhotoVersion++;
        await _db.SaveChangesAsync();

        var url = ProfileDisplay.PhotoUrl(profile.PhotoFileName, profile.PhotoVersion);
        if (IsAjax()) return Json(new { success = true, url });
        TempData["Toast"] = "success|Photo updated.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/RemovePhoto ────────────────────────

    /// <summary>
    /// Deletes the profile photo (file + reference) so the avatar falls back to initials.
    /// Returns JSON <c>{ success, initials }</c> for the fetch caller, or redirects for a plain post.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemovePhoto()
    {
        var userId  = UserId();
        var profile = await GetOrCreateProfileAsync(userId);
        DeletePhotoFile(profile);
        profile.PhotoFileName = null;
        profile.PhotoVersion++;
        await _db.SaveChangesAsync();

        var user = await _userManager.FindByIdAsync(userId);
        var initials = ProfileDisplay.Initials(profile.FullName, user?.Email);
        if (IsAjax()) return Json(new { success = true, initials });
        TempData["Toast"] = "success|Photo removed.";
        return RedirectToAction(nameof(Index));
    }

    private IActionResult PhotoError(string error)
    {
        if (IsAjax()) return BadRequest(new { success = false, error });
        TempData["Error"] = error;
        return RedirectToAction(nameof(Index));
    }

    private bool IsAjax() => Request.Headers.XRequestedWith == "XMLHttpRequest";

    /// <summary>Removes the stored photo file (if any) without touching the profile row.</summary>
    private void DeletePhotoFile(UserProfile profile)
    {
        if (profile.PhotoFileName is null) return;
        var old = Path.Combine(_uploads.PhotosDirectory, profile.PhotoFileName);
        if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
    }

    // ── POST /Profile/SaveSkills ─────────────────────────

    /// <summary>
    /// Auto-saves the tag-based skills list via AJAX, deduplicated (case-insensitive, whitespace
    /// normalised, first-seen casing kept — see <see cref="ProfileTags"/>) and JSON-encoded into
    /// <c>UserProfile.SkillsJson</c>. Called immediately whenever a skill tag is added or removed
    /// on the Profile page, so there's no separate "Save" button or page reload. A stale client or
    /// direct API call can't sneak a duplicate in, and previously stored duplicates collapse here.
    /// </summary>
    /// <param name="skillsJson">A JSON array of skill strings from the tag input widget.</param>
    /// <returns>JSON <c>{ success: true, skills }</c> — <c>skills</c> is the normalised list as stored.</returns>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSkills(string? skillsJson)
    {
        var userId = UserId();
        var profile = await GetOrCreateProfileAsync(userId);
        var skills = ProfileTags.Dedupe(ProfileTags.FromJson(skillsJson));
        profile.SkillsJson = ProfileTags.ToJson(skills);
        await _db.SaveChangesAsync();
        return Json(new { success = true, skills });
    }

    // ── POST /Profile/SaveTargetRoles ────────────────────

    /// <summary>
    /// Auto-saves the tag-based target-roles list via AJAX, deduplicated the same way as
    /// <see cref="SaveSkills"/> and JSON-encoded into <c>UserProfile.TargetRolesJson</c>. Called
    /// immediately whenever a role tag is added or removed on the Profile page (including from the
    /// searchable role combobox).
    /// </summary>
    /// <param name="targetRolesJson">A JSON array of role-name strings from the tag input widget.</param>
    /// <returns>JSON <c>{ success: true, targetRoles }</c> — <c>targetRoles</c> is the normalised list as stored.</returns>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTargetRoles(string? targetRolesJson)
    {
        var userId = UserId();
        var profile = await GetOrCreateProfileAsync(userId);
        var targetRoles = ProfileTags.Dedupe(ProfileTags.FromJson(targetRolesJson));
        profile.TargetRolesJson = ProfileTags.ToJson(targetRoles);
        await _db.SaveChangesAsync();
        return Json(new { success = true, targetRoles });
    }

    // ── POST /Profile/ReparseResume ──────────────────────

    /// <summary>
    /// Re-runs the parse against the active resume and sends the user to the review screen — the
    /// "Analyze with AI" button on the Resume card. Replaces the old <c>AnalyzeResume</c>, which
    /// merged straight into the profile; there is deliberately no endpoint left that does that.
    /// </summary>
    /// <remarks>
    /// Costs nothing extra to run: the resume's text was extracted once at upload and cached on the
    /// version (<see cref="ResumeTextService"/>), so re-parsing needs no re-upload and no re-read of
    /// the file. It does spend one AI permit, hence the <c>"ai"</c> policy.
    /// </remarks>
    [HttpPost, ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> ReparseResume()
    {
        var userId = UserId();

        var version = await _db.ResumeVersions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == userId && r.IsActive);

        if (version is null)
        {
            TempData["Toast"] = "error|No active resume found. Upload a resume and set it as active first.";
            return RedirectToAction(nameof(Index));
        }

        var text = await _resumeText.GetAsync(version);
        if (!text.Ok)
        {
            TempData["Toast"] = $"error|{ResumeTextError(text.Status)}";
            return RedirectToAction(nameof(Index));
        }

        var parse = await ParseAsync(userId, text, version.Id);
        if (!parse.Success)
        {
            TempData["Toast"] = $"error|Couldn't analyze your resume: {parse.Error?.TrimEnd('.')}.";
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(ReviewResume), new { id = parse.DraftId });
    }

    // ── GET /Profile/ReviewResume ────────────────────────

    /// <summary>
    /// The screen every AI-derived profile write goes through. Shows what the parse proposed — each
    /// skill with the resume snippet behind it — and writes nothing until the user posts it back.
    /// </summary>
    /// <param name="id">A specific draft (owner-scoped 404); omitted, the newest one awaiting review.</param>
    [HttpGet]
    public async Task<IActionResult> ReviewResume(int? id)
    {
        var userId = UserId();

        var draft = id is null
            ? await _parser.PendingAsync(userId)
            : await _parser.ByIdAsync(userId, id.Value);

        // A foreign or missing id is a 404, not a 403 — same rule as every other id-taking endpoint
        // (CLAUDE.md §8), so draft ids can't be probed.
        if (draft is null)
        {
            if (id is not null) return NotFound();
            TempData["Toast"] = "info|Nothing to review — upload a resume or use Analyze with AI first.";
            return RedirectToAction(nameof(Index));
        }

        if (draft.Applied)
        {
            TempData["Toast"] = "info|That resume analysis has already been applied to your profile.";
            return RedirectToAction(nameof(Index));
        }

        return View(await BuildReviewViewModelAsync(userId, draft));
    }

    // ── POST /Profile/ApplyResumeReview ──────────────────

    /// <summary>
    /// Merges what the user confirmed into the profile: tags are added, never removed, and an
    /// unchecked skill is never written. No model call, so no rate limit.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyResumeReview(ResumeReviewInput input)
    {
        var userId = UserId();
        var draft = await _parser.ByIdAsync(userId, input.DraftId);
        if (draft is null) return NotFound();

        var result = await _autoFill.ApplyAsync(userId, draft, input.ToReviewed());
        if (!result.Success)
        {
            TempData["Toast"] = $"error|{result.Error}";
            return RedirectToAction(nameof(Index));
        }

        // On the shared demo account, mark this browser as the one that applied so the redirect back to
        // /Profile shows the merge instead of undoing it. The stamp is read back from the database rather
        // than taken from memory, so it matches exactly what the next request will compare against.
        if (AiRateLimiting.IsDemoUser(User, _config))
        {
            var enrichedAt = await _db.UserProfiles.AsNoTracking()
                .Where(p => p.UserId == userId)
                .Select(p => p.ProfileLastEnrichedAt)
                .FirstOrDefaultAsync();

            if (enrichedAt is { } stamp) DemoProfileReset.Remember(Response, stamp);
        }

        // The page reloads after the redirect; this tells it which chips are new so it can flash them.
        if (result.SkillsAdded > 0 || result.RolesAdded > 0)
            TempData["AutoFillAdded"] = JsonSerializer.Serialize(new { skills = result.AddedSkills, roles = result.AddedRoles });

        TempData["Toast"] = result.AddedAnything
            ? $"success|Profile updated — {result.Summary}."
            : "info|Nothing new to add — your profile already had everything you kept.";

        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/DiscardResumeReview ────────────────

    /// <summary>
    /// Throws the draft away without touching the profile. The guarantee this endpoint carries is
    /// that the profile is byte-identical afterwards — pinned by <c>ResumeReviewTests</c>.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DiscardResumeReview(int draftId)
    {
        var userId = UserId();
        var draft = await _db.ParsedResumes.FirstOrDefaultAsync(p => p.Id == draftId && p.UserId == userId);
        if (draft is null) return NotFound();

        _db.ParsedResumes.Remove(draft);
        await _db.SaveChangesAsync();

        TempData["Toast"] = "info|Discarded — your profile is unchanged.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/UploadResume ───────────────────────

    /// <summary>
    /// Uploads a new resume PDF as the next version (see <see cref="UploadResumeAsync"/> for validation
    /// rules), then parses it into a review draft and sends the user to <see cref="ReviewResume"/>.
    /// The profile is not touched here — see <see cref="ParseAfterUploadAsync"/>.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadResume(IFormFile? resume)
        => await UploadResumeAsync(resume);

    // ── POST /Profile/SetActiveResume ────────────────────

    /// <summary>Marks one resume version as active (used by AI matching/scoring) and unmarks all others for this user.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActiveResume(int id)
    {
        var userId = UserId();
        var versions = await _db.ResumeVersions.Where(r => r.UserId == userId).ToListAsync();
        if (!versions.Any(v => v.Id == id)) return NotFound();
        foreach (var v in versions) v.IsActive = v.Id == id;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Active resume updated.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/RenameResume (AJAX) ────────────────

    /// <summary>
    /// Sets or clears the optional label on one resume version (inline edit on the profile's resume
    /// list). A blank label reverts to the file name. Returns JSON <c>{ success, label }</c> where
    /// <c>label</c> is what the UI should now show.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameResume(int id, string? label)
    {
        var userId = UserId();
        var resume = await _db.ResumeVersions.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (resume == null) return NotFound(new { success = false, error = "Resume not found." });

        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label is { Length: > 60 })
            return BadRequest(new { success = false, error = "Keep the name under 60 characters." });

        resume.Label = label;
        await _db.SaveChangesAsync();
        return Json(new { success = true, label = resume.DisplayName });
    }

    // ── POST /Profile/DeleteResume ───────────────────────

    /// <summary>Deletes a resume version (file + DB row) and renumbers the remaining versions so they stay contiguous.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteResume(int id)
    {
        var userId = UserId();
        var resume = await _db.ResumeVersions.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (resume == null) return NotFound();

        DeleteFile(resume.StoredPath);
        _db.ResumeVersions.Remove(resume);
        await _db.SaveChangesAsync();
        // Renumber remaining versions
        await RenumberVersionsAsync(userId);
        TempData["Success"] = "Resume deleted.";
        return RedirectToAction(nameof(Index));
    }

    // ── GET /Profile/DownloadResume/{id} ─────────────────

    /// <summary>Streams a resume PDF version back to the browser under its original filename.</summary>
    /// <returns>The PDF file, or 404 if it doesn't exist or isn't owned by the current user.</returns>
    [HttpGet]
    public async Task<IActionResult> DownloadResume(int id)
    {
        var userId = UserId();
        var resume = await _db.ResumeVersions.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (resume == null) return NotFound();
        return ServeFile(resume.StoredPath, resume.OriginalFileName);
    }

    // ── POST /Profile/ScoreResume ────────────────────────

    /// <summary>
    /// Extracts text from the user's active resume PDF and sends it to <see cref="ResumeScoreService"/>
    /// for an AI quality score and improvement suggestions. Re-renders the Index view with the result
    /// attached rather than redirecting, so the page doesn't need a second round-trip to show it.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> ScoreResume()
    {
        var userId = UserId();
        var vm = await BuildViewModelAsync();

        var text = await _resumeText.GetActiveAsync(userId);
        if (!text.Ok)
        {
            TempData["ScoreError"] = text.Status switch
            {
                ResumeTextStatus.NoResume    => "No active resume found. Upload a resume and set it as active first.",
                ResumeTextStatus.FileMissing => "Resume file not found. Try uploading it again.",
                ResumeTextStatus.Unreadable  => "Could not read the PDF. Make sure it is a text-based (not scanned) PDF.",
                _                            => "No readable text found in the PDF."
            };
            return RedirectToAction(nameof(Index));
        }

        vm.ScoreResult = await _scorer.ScoreAsync(text.Text!, vm.TargetRoles,
            await _userContext.BuildAsync(UserId(), HttpContext.RequestAborted));

        // Re-load full vm and attach result
        var fullVm = await BuildViewModelAsync();
        fullVm.ScoreResult = vm.ScoreResult;
        return View(nameof(Index), fullVm);
    }

    // ── POST /Profile/AutoMatch (AJAX) ───────────────────

    /// <summary>
    /// Scores the user's active resume against a job description, called via fetch from the Job
    /// Application create page right after the AI job analyzer fills out the form. Antiforgery
    /// validation is skipped because this is a same-origin AJAX call from an already-authenticated
    /// page with no cookie-based state to protect beyond the auth cookie itself.
    /// </summary>
    /// <param name="request">The job description text to match the resume against.</param>
    /// <returns>
    /// JSON shaped as <c>{ hasResume, success, score, recommendation, matchingSkills, missingSkills,
    /// summary, error }</c>. Note that ASP.NET Core's default JSON policy lowercases these from their
    /// PascalCase C# names — the calling JavaScript must read them as camelCase.
    /// </returns>
    [HttpPost, IgnoreAntiforgeryToken] // API endpoint called via AJAX from authenticated Create page
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> AutoMatch([FromBody] AutoMatchRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.JobDescription))
            return Json(new { hasResume = false });

        var userId = UserId();

        var text = await _resumeText.GetActiveAsync(userId);

        // "No resume" and "the file went missing" are both "there is nothing to match against" to this page.
        if (text.Status is ResumeTextStatus.NoResume or ResumeTextStatus.FileMissing)
            return Json(new { hasResume = false });

        if (text.Status == ResumeTextStatus.Unreadable)
            return Json(new { hasResume = true, success = false, error = "Could not read the active resume." });

        if (!text.Ok)
            return Json(new { hasResume = true, success = false, error = "No readable text in the active resume." });

        var result = await _matcher.MatchAsync(text.Text!, request.JobDescription,
            await _userContext.BuildAsync(UserId(), HttpContext.RequestAborted));
        return Json(new
        {
            hasResume      = true,
            result.Success,
            result.Score,
            result.Recommendation,
            result.MatchingSkills,
            result.MissingSkills,
            result.Summary,
            result.Error
        });
    }

    // ── POST /Profile/RewriteBullet (AJAX) ───────────────

    public sealed class RewriteBulletRequest
    {
        public string? Bullet { get; set; }
        public int ApplicationId { get; set; }
    }

    /// <summary>
    /// "Rewrite a bullet": 2–3 rewrites of one pasted resume bullet aimed at one of the user's applications, via
    /// <see cref="ResumeRewriteService"/>. JSON fetch from <c>wwwroot/js/bullet-rewriter.js</c> (antiforgery token in the
    /// header). Owner-scoped: another user's application is a 404. An application without a stored job description gets
    /// <c>needsDescription</c> and a link to edit it. The demo account gets fixed sample rewrites without a model call,
    /// so the action carries <see cref="NoAiCallForDemoAttribute"/>. Nothing is stored.
    /// </summary>
    /// <returns>JSON <c>{ success, variants:[{text, angle}], demo, sampleBullet }</c> or <c>{ success:false, error, field?, needsDescription?, editUrl? }</c>.</returns>
    [HttpPost, ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    [NoAiCallForDemo]
    public async Task<IActionResult> RewriteBullet([FromBody] RewriteBulletRequest? req, CancellationToken ct)
    {
        var bullet = ResumeRewriteService.NormalizeBullet(req?.Bullet);
        if (bullet.Length == 0)
            return Json(new { success = false, field = "bullet", error = "Paste a bullet to rewrite." });
        if (bullet.Length > ResumeRewriteService.MaxBulletChars)
            return Json(new { success = false, field = "bullet", error = $"Keep the bullet under {ResumeRewriteService.MaxBulletChars} characters. Rewrite one bullet at a time." });

        var context = await _rewriter.BuildContextAsync(req!.ApplicationId, UserId(), ct);
        if (context is null)
            return NotFound(new { success = false, error = "Application not found." });
        if (!context.HasJobDescription)
            return Json(new
            {
                success = false,
                needsDescription = true,
                editUrl = Url.Action("Edit", "JobApplications", new { id = context.ApplicationId }),
                error = ResumeRewriteService.NoJobDescriptionError
            });

        if (AiRateLimiting.IsDemoUser(User, _config))
            return Json(new
            {
                success = true,
                demo = true,
                sampleBullet = ResumeRewriteService.DemoSampleBullet,
                variants = ResumeRewriteService.DemoVariants().Select(v => new { text = v.Text, angle = v.Angle })
            });

        var result = await _rewriter.RewriteAsync(bullet, context, ct);
        return result.Success
            ? Json(new
              {
                  success  = true,
                  demo     = false,
                  variants = result.Variants.Select(v => new { text = v.Text, angle = v.Angle }),
                  // Only when a guard actually dropped something: a model that simply returned two variants isn't a discard.
                  note     = result.Discarded > 0 ? ResumeRewriteService.DiscardedNote : null
              })
            : Json(new { success = false, error = result.Error });
    }

    // ── Helpers ──────────────────────────────────────────

    /// <summary>Resolves the current signed-in user's id from the auth claims.</summary>
    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Fetches the user's profile row, creating (but not yet saving) an empty one if none exists.</summary>
    private async Task<UserProfile> GetOrCreateProfileAsync(string userId)
    {
        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile == null)
        {
            profile = new UserProfile { UserId = userId };
            _db.UserProfiles.Add(profile);
        }
        return profile;
    }

    /// <summary>
    /// Assembles everything the profile page needs in one pass: personal info, documents, parsed
    /// skill/role tags, and derived application stats (success rate, this-month count, upcoming
    /// deadlines within 7 days, and the 5 most recently AI-matched applications).
    /// </summary>
    private async Task<ProfileViewModel> BuildViewModelAsync()
    {
        var userId = UserId();
        var user   = await _userManager.FindByIdAsync(userId);
        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId)
                      ?? new UserProfile { UserId = userId };

        var resumes = await _db.ResumeVersions
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.VersionNumber)
            .ToListAsync();

        var apps = await _db.JobApplications
            .Where(a => a.UserId == userId)
            .ToListAsync();

        var statusCounts = Enum.GetValues<ApplicationStatus>()
            .ToDictionary(s => s, s => apps.Count(a => a.Status == s));

        int offers = statusCounts.GetValueOrDefault(ApplicationStatus.Offer);
        double successRate = apps.Count > 0 ? Math.Round(offers * 100.0 / apps.Count, 1) : 0;

        var today = (await _clocks.GetAsync()).Today;
        var appsThisMonth = apps.Count(a =>
            a.DateApplied.HasValue &&
            a.DateApplied.Value.Year  == today.Year &&
            a.DateApplied.Value.Month == today.Month);

        var upcomingDeadlines7 = apps
            .Where(a => a.Deadline.HasValue
                     && a.Deadline.Value.Date >= today
                     && a.Deadline.Value.Date <= today.AddDays(7)
                     && a.Status != ApplicationStatus.Rejected)
            .OrderBy(a => a.Deadline)
            .ToList();

        var recentMatched = apps
            .Where(a => a.MatchScore.HasValue)
            .OrderByDescending(a => a.Id)
            .Take(5)
            .ToList();

        List<GitHubRepoDto>? githubRepos = null;
        if (!string.IsNullOrWhiteSpace(profile.GitHubUsername))
            githubRepos = await _github.GetPublicReposAsync(profile.GitHubUsername);

        var gmailConfigured = GmailIntegration.IsConfigured(_config);
        var gmail = gmailConfigured
            ? await _db.GmailConnections.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId)
            : null;

        return new ProfileViewModel
        {
            GmailConfigured = gmailConfigured,
            GmailConnection = gmail,
            IsDemoAccount   = AiRateLimiting.IsDemoEmail(user?.Email, _config),
            RewriteApplications = apps
                .Where(a => !string.IsNullOrWhiteSpace(a.JobDescription))
                .OrderByDescending(a => a.Id)
                .Select(a => new RewriteApplicationOption(a.Id, a.CompanyName, a.RoleTitle))
                .ToList(),
            ResumeStats   = ResumeAnalyticsService.Build(resumes, apps).ByResumeId,
            Profile       = profile,
            Email         = user?.Email,
            Resumes       = resumes,
            Skills        = ProfileTags.FromJson(profile.SkillsJson),
            TargetRoles   = ProfileTags.FromJson(profile.TargetRolesJson),
            RoleSuggestions = _roleSeeds.All(),
            PendingParse  = await _parser.PendingAsync(userId),
            TotalApplications = apps.Count,
            StatusCounts  = statusCounts,
            SuccessRate   = successRate,
            ApplicationsThisMonth  = appsThisMonth,
            UpcomingDeadlines7Days = upcomingDeadlines7,
            RecentMatchedApps      = recentMatched,
            GitHubRepos            = githubRepos,
            CalendarFeedUrl        = CalendarFeedUrl(profile.CalendarToken)
        };
    }

    /// <summary>
    /// Assembles the review screen: the parse's proposal, the profile as it stands so each row can say
    /// what it would replace, and which tags are already present so nothing is offered twice.
    /// </summary>
    private async Task<ResumeReviewViewModel> BuildReviewViewModelAsync(string userId, ParsedResume draft)
    {
        var parsed  = ParsedProfile.FromJsonOrEmpty(draft.RawJson);
        var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId)
                      ?? new UserProfile { UserId = userId };

        var source = draft.ResumeVersionId is { } versionId
            ? await _db.ResumeVersions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == versionId && r.UserId == userId)
            : null;

        return new ResumeReviewViewModel
        {
            DraftId        = draft.Id,
            CreatedAt      = draft.CreatedAt,
            Parsed         = parsed,
            Skills         = parsed.Skills.Select(s => new ReviewSkillRow(s.Name, s.Evidence, s.Confidence)).ToList(),
            TargetRoles    = parsed.TargetRoles,
            Current        = profile,
            ExistingSkills = new HashSet<string>(ProfileTags.FromJson(profile.SkillsJson), StringComparer.OrdinalIgnoreCase),
            ExistingRoles  = new HashSet<string>(ProfileTags.FromJson(profile.TargetRolesJson), StringComparer.OrdinalIgnoreCase),
            Source         = source,
            IsDemoAccount  = AiRateLimiting.IsDemoUser(User, _config)
        };
    }

    /// <summary>
    /// Resume upload pipeline: validates size and identifies the file by its bytes (rejects files
    /// merely renamed to .pdf or .docx), stores it under a per-user directory with a random filename
    /// (avoids collisions and leaking the original name on disk), and records it as the next
    /// version number. The first version uploaded is auto-activated.
    /// </summary>
    private async Task<IActionResult> UploadResumeAsync(IFormFile? file)
    {
        const string label = "Resume";

        if (file == null || file.Length == 0)
        {
            TempData["Error"] = $"Please select a file to upload.";
            return RedirectToAction(nameof(Index));
        }

        if (file.Length > ResumeFileType.MaxBytes)
        {
            TempData["Error"] = $"{label} must be under 5 MB.";
            return RedirectToAction(nameof(Index));
        }

        // Identified by its contents, never by its name: the extension, the accept attribute and the
        // client-sent content type are all a rename away, so an .exe called resume.pdf has to die here.
        ResumeFormat format;
        await using (var checkStream = file.OpenReadStream())
        {
            format = await ResumeFileType.DetectAsync(checkStream);
        }

        if (format == ResumeFormat.Unknown)
        {
            TempData["Error"] = $"{label} must be a {ResumeFileType.AcceptDescription} file.";
            return RedirectToAction(nameof(Index));
        }

        var userId = UserId();
        const string subDir = "resumes";
        var dir = _uploads.GetUserDirectory(subDir, userId);

        var stored = $"{Guid.NewGuid():N}{ResumeFileType.ExtensionFor(format)}";
        var fullPath = Path.Combine(dir, stored);
        // Scoped so the stream is flushed and closed before the auto-fill below reads the file back.
        await using (var fs = System.IO.File.Create(fullPath))
        {
            await file.CopyToAsync(fs);
        }

        var relativePath = UploadStorage.MakeStoredPath(subDir, userId, stored);

        var safeOriginalName = SanitizeFileName(file.FileName);

        int nextVersion = (await _db.ResumeVersions.Where(r => r.UserId == userId).MaxAsync(r => (int?)r.VersionNumber) ?? 0) + 1;
        bool firstOne = nextVersion == 1;
        var version = new ResumeVersion
        {
            UserId           = userId,
            VersionNumber    = nextVersion,
            OriginalFileName = safeOriginalName,
            StoredPath       = relativePath,
            FileSize         = file.Length,
            IsActive         = firstOne
        };
        _db.ResumeVersions.Add(version);

        await _db.SaveChangesAsync();

        // Extract once, here, so no later read of this version has to parse the file. An unreadable file
        // leaves the column null and is reported below; the upload itself still succeeded.
        var text = await _resumeText.StoreAsync(version);

        // The file is saved at this point no matter what the parse does — that has always been the rule,
        // and it is why this action is not behind the "ai" policy (a spent bucket must not lose the upload).
        var parse = await ParseAfterUploadAsync(userId, text, version.Id);
        if (parse.Success)
            return RedirectToAction(nameof(ReviewResume), new { id = parse.DraftId });

        TempData["Toast"] = $"info|{parse.Error}";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Parses a just-uploaded resume into a review draft, turning every failure into a sentence the
    /// upload toast can say. <b>The upload has already succeeded by the time this runs</b>, so no
    /// branch here may undo it: a demo account, an empty AI bucket, a scanned PDF and a model error
    /// all keep the file and send the user back to the profile with a reason.
    /// </summary>
    private async Task<ResumeParseResult> ParseAfterUploadAsync(string userId, ResumeTextResult text, int versionId)
    {
        const string uploaded = "Resume uploaded";

        if (!text.Ok)
            return ResumeParseResult.Failed($"{uploaded}, but we couldn't read it ({ResumeTextError(text.Status).TrimEnd('.')}). Use Analyze with AI to retry.");

        if (ResumeTextService.LooksScanned(text))
            return ResumeParseResult.Failed($"{uploaded}, but we couldn't read much text from it — it may be a scanned image. Try exporting your resume as a text-based PDF or a Word document.");

        // The demo account sees the real review screen from a canned parse: no model call, no permit.
        if (AiRateLimiting.IsDemoUser(User, _config))
            return await _parser.StoreDemoParseAsync(userId, versionId, text.Text!.Length, HttpContext.RequestAborted);

        // One permit from the shared "ai" bucket, taken by hand because this action is not policied —
        // an empty bucket must cost the parse, never the upload.
        using var lease = _aiLimiter.TryAcquire(userId, isDemo: false);
        if (!lease.IsAcquired)
        {
            TimeSpan? retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? ra : null;
            var limitMsg = AiRateLimiting.BuildMessage(_aiLimiter.LimitFor(false), _aiLimiter.WindowMinutes, retryAfter);
            return ResumeParseResult.Failed($"{uploaded}, but we couldn't analyze it: {limitMsg} Use Analyze with AI to retry later.");
        }

        var parse = await SafeParseAsync(userId, text.Text!, versionId);
        return parse.Success
            ? parse
            : ResumeParseResult.Failed($"{uploaded}, but we couldn't analyze it ({parse.Error?.TrimEnd('.')}). Use Analyze with AI to retry.");
    }

    /// <summary>
    /// The re-parse path's call. The demo branch is the same canned draft; unlike the upload path
    /// this action is behind the <c>"ai"</c> policy, so the permit has already been taken for it.
    /// </summary>
    private async Task<ResumeParseResult> ParseAsync(string userId, ResumeTextResult text, int versionId)
    {
        if (ResumeTextService.LooksScanned(text))
            return ResumeParseResult.Failed("we couldn't read much text from that file — it may be a scanned image");

        if (AiRateLimiting.IsDemoUser(User, _config))
            return await _parser.StoreDemoParseAsync(userId, versionId, text.Text!.Length, HttpContext.RequestAborted);

        return await SafeParseAsync(userId, text.Text!, versionId);
    }

    /// <summary>
    /// Runs the parse and turns an unexpected throw into a failed result. A parse that blows up must
    /// not become a 500 on a page whose upload already succeeded.
    /// </summary>
    private async Task<ResumeParseResult> SafeParseAsync(string userId, string text, int versionId)
    {
        try
        {
            return await _parser.ParseAsync(userId, text, versionId, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resume parse threw for user {UserId}.", userId);
            return ResumeParseResult.Failed("something went wrong while reading it");
        }
    }

    /// <summary>Why the active resume produced no usable text, as a full sentence for a JSON error field.</summary>
    private static string ResumeTextError(ResumeTextStatus status) => status switch
    {
        ResumeTextStatus.FileMissing => "Resume file not found. Try uploading it again.",
        ResumeTextStatus.Unreadable  => "Could not read the PDF. Make sure it is a text-based (not scanned) PDF.",
        _                            => "No readable text found in the PDF."
    };

    /// <summary>Re-sequences version numbers to 1..N after a deletion so they stay contiguous (no gaps).</summary>
    private async Task RenumberVersionsAsync(string userId)
    {
        var versions = await _db.ResumeVersions.Where(r => r.UserId == userId).OrderBy(r => r.Id).ToListAsync();
        for (int i = 0; i < versions.Count; i++) versions[i].VersionNumber = i + 1;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Serves a stored resume as a physical file response. Documents live under the uploads
    /// root (see <see cref="UploadStorage"/>) and are never mapped as static files — this
    /// method is the only path that exposes them, and it's only called after an ownership check.
    /// </summary>
    /// <remarks>
    /// The media type comes from the <b>stored</b> path's extension, which <see cref="ResumeFileType"/>
    /// wrote after inspecting the bytes — never from the name the file was uploaded under, which is
    /// attacker-controlled.
    /// </remarks>
    private IActionResult ServeFile(string storedPath, string originalName)
    {
        var fullPath = _uploads.Resolve(storedPath);
        if (!System.IO.File.Exists(fullPath)) return NotFound();
        return PhysicalFile(fullPath, ResumeFileType.MediaTypeFor(storedPath), originalName);
    }

    /// <summary>Deletes a stored file from disk if it exists; no-ops otherwise.</summary>
    private void DeleteFile(string storedPath) => _uploads.Delete(storedPath);

    /// <summary>Strips path components and characters invalid in filenames, so an uploaded name can't be used for path traversal.</summary>
    private static string SanitizeFileName(string name)
    {
        var sanitized = Path.GetFileName(name);
        var invalid = Path.GetInvalidFileNameChars();
        sanitized = new string(sanitized.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "upload.pdf" : sanitized;
    }
}

public record AutoMatchRequest(string JobDescription);
