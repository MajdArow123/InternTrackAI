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
/// resume version history (upload, activate, delete, download), the resume → profile auto-fill
/// (automatic after an upload, manual via "Analyze with AI"), AI resume scoring, and the AI
/// resume-match endpoint used by the Job Application create page, and the "Rewrite a bullet" tool.
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
        _aiLimiter = aiLimiter;
        _github = github;
        _logger = logger;
    }

    // ── GET /Profile ────────────────────────────────────

    /// <summary>Renders the full profile page: personal info, documents, skills, and application stats.</summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        await EnsureCalendarTokenAsync(UserId());
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
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveInfo(string? fullName, string? displayName, string? country, string? phoneNumber, string? githubUsername, string? timeZoneId)
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

        await _db.SaveChangesAsync();
        var clock = UserClock.For(profile.TimeZoneId);   // fresh, not the request-cached clock: the zone may have just changed
        return Json(new { success = true, timeZoneId = profile.TimeZoneId, nowLocal = clock.LocalTime(clock.NowUtc) });
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

    // ── POST /Profile/AnalyzeResume (AJAX) ───────────────

    /// <summary>
    /// Manual re-run of the resume → profile auto-fill against the active resume (the same
    /// <see cref="ProfileAutoFillService"/> that runs automatically after an upload): an empty name
    /// is filled in, extracted skills and target roles are added when not already present, nothing
    /// is ever removed. The merge is persisted here; the response carries the profile's values after
    /// the merge so the page can re-render the chips without a reload.
    /// </summary>
    /// <returns>
    /// JSON <c>{ success, hasResume, fullName, skills, targetRoles, nameFilled, skillsAdded, rolesAdded,
    /// addedSkills, addedRoles, summary, error }</c>. <c>hasResume</c> is false if no active resume exists yet (nothing to analyze).
    /// </returns>
    [HttpPost, ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> AnalyzeResume()
    {
        var userId = UserId();
        var activeResume = await _db.ResumeVersions.FirstOrDefaultAsync(r => r.UserId == userId && r.IsActive);
        if (activeResume == null)
            return Json(new { success = false, hasResume = false });

        var result = await _autoFill.FillFromResumeAsync(userId, activeResume.StoredPath);
        if (!result.Success)
            return Json(new { success = false, hasResume = true, error = result.Error });

        return Json(new
        {
            success     = true,
            hasResume   = true,
            fullName    = result.FullName,
            skills      = result.Skills,
            targetRoles = result.TargetRoles,
            nameFilled  = result.NameFilled,
            skillsAdded = result.SkillsAdded,
            rolesAdded  = result.RolesAdded,
            addedSkills = result.AddedSkills,
            addedRoles  = result.AddedRoles,
            summary     = result.Summary
        });
    }

    // ── POST /Profile/UploadResume ───────────────────────

    /// <summary>
    /// Uploads a new resume PDF as the next version (see <see cref="UploadResumeAsync"/> for validation
    /// rules), then auto-fills the profile from it (see <see cref="AutoFillToastAsync"/>).
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

        var activeResume = vm.Resumes.FirstOrDefault(r => r.IsActive);
        if (activeResume == null)
        {
            TempData["ScoreError"] = "No active resume found. Upload a resume and set it as active first.";
            return RedirectToAction(nameof(Index));
        }

        var filePath = _uploads.Resolve(activeResume.StoredPath);
        if (!System.IO.File.Exists(filePath))
        {
            TempData["ScoreError"] = "Resume file not found. Try uploading it again.";
            return RedirectToAction(nameof(Index));
        }

        string resumeText;
        try
        {
            await using var fs = System.IO.File.OpenRead(filePath);
            resumeText = ResumeMatcherService.ExtractPdfText(fs);
        }
        catch
        {
            TempData["ScoreError"] = "Could not read the PDF. Make sure it is a text-based (not scanned) PDF.";
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(resumeText) || resumeText.Length < 50)
        {
            TempData["ScoreError"] = "No readable text found in the PDF.";
            return RedirectToAction(nameof(Index));
        }

        vm.ScoreResult = await _scorer.ScoreAsync(resumeText, vm.TargetRoles);

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

        var activeResume = await _db.ResumeVersions
            .Where(r => r.UserId == userId && r.IsActive)
            .FirstOrDefaultAsync();

        if (activeResume == null)
            return Json(new { hasResume = false });

        var filePath = _uploads.Resolve(activeResume.StoredPath);
        if (!System.IO.File.Exists(filePath))
            return Json(new { hasResume = false });

        string resumeText;
        try
        {
            await using var fs = System.IO.File.OpenRead(filePath);
            resumeText = ResumeMatcherService.ExtractPdfText(fs);
        }
        catch
        {
            return Json(new { hasResume = true, success = false, error = "Could not read the active resume." });
        }

        if (string.IsNullOrWhiteSpace(resumeText) || resumeText.Length < 50)
            return Json(new { hasResume = true, success = false, error = "No readable text in the active resume." });

        var result = await _matcher.MatchAsync(resumeText, request.JobDescription);
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
            ? Json(new { success = true, demo = false, variants = result.Variants.Select(v => new { text = v.Text, angle = v.Angle }) })
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
    /// Resume upload pipeline: validates extension, size, and PDF magic bytes (rejects files
    /// merely renamed to .pdf), stores the file under a per-user directory with a random filename
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

        if (file.Length > 5 * 1024 * 1024)
        {
            TempData["Error"] = $"{label} must be under 5 MB.";
            return RedirectToAction(nameof(Index));
        }

        if (!Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = $"{label} must be a PDF file.";
            return RedirectToAction(nameof(Index));
        }

        // Validate PDF magic bytes (%PDF = 0x25 0x50 0x44 0x46)
        var magic = new byte[4];
        await using (var checkStream = file.OpenReadStream())
        {
            var read = await checkStream.ReadAsync(magic.AsMemory(0, 4));
            if (read < 4 || magic[0] != 0x25 || magic[1] != 0x50 || magic[2] != 0x44 || magic[3] != 0x46)
            {
                TempData["Error"] = $"{label} is not a valid PDF file.";
                return RedirectToAction(nameof(Index));
            }
        }

        var userId = UserId();
        const string subDir = "resumes";
        var dir = _uploads.GetUserDirectory(subDir, userId);

        var stored = $"{Guid.NewGuid():N}.pdf";
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
        _db.ResumeVersions.Add(new ResumeVersion
        {
            UserId           = userId,
            VersionNumber    = nextVersion,
            OriginalFileName = safeOriginalName,
            StoredPath       = relativePath,
            FileSize         = file.Length,
            IsActive         = firstOne
        });

        await _db.SaveChangesAsync();

        // The file is saved at this point no matter what the auto-fill does.
        TempData["Toast"] = await AutoFillToastAsync(userId, relativePath);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Runs the resume → profile auto-fill for a just-uploaded file and turns the outcome into the
    /// upload toast ("type|message"). The upload has already succeeded, so every branch here still
    /// reports that: the demo account skips the AI call, a rate-limited or failed extraction says the
    /// profile wasn't auto-filled and points at "Analyze with AI" for a retry. One permit is taken
    /// from the user's shared "ai" bucket — the same one the AI endpoints draw from.
    /// </summary>
    private async Task<string> AutoFillToastAsync(string userId, string storedPath)
    {
        const string uploaded = "Resume uploaded";

        if (AiRateLimiting.IsDemoUser(User, _config))
            return $"info|{uploaded}. Auto-fill is skipped on the demo account — use Analyze with AI to fill in your profile.";

        using var lease = _aiLimiter.TryAcquire(userId, isDemo: false);
        if (!lease.IsAcquired)
        {
            TimeSpan? retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? ra : null;
            var limitMsg = AiRateLimiting.BuildMessage(_aiLimiter.LimitFor(false), _aiLimiter.WindowMinutes, retryAfter);
            return $"info|{uploaded}, but your profile wasn't auto-filled: {limitMsg} Use Analyze with AI to retry later.";
        }

        ProfileAutoFillResult result;
        try
        {
            result = await _autoFill.FillFromResumeAsync(userId, storedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resume auto-fill threw for user {UserId}; the upload itself succeeded.", userId);
            result = ProfileAutoFillResult.Failed("something went wrong while reading it");
        }

        if (!result.Success)
            return $"info|{uploaded}, but your profile wasn't auto-filled ({result.Error?.TrimEnd('.')}). Use Analyze with AI to retry.";

        // The page reloads after the redirect; this tells it which chips are new so it can flash them.
        if (result.SkillsAdded > 0 || result.RolesAdded > 0)
            TempData["AutoFillAdded"] = JsonSerializer.Serialize(new { skills = result.AddedSkills, roles = result.AddedRoles });

        return $"success|{uploaded} — {result.Summary}";
    }

    /// <summary>Re-sequences version numbers to 1..N after a deletion so they stay contiguous (no gaps).</summary>
    private async Task RenumberVersionsAsync(string userId)
    {
        var versions = await _db.ResumeVersions.Where(r => r.UserId == userId).OrderBy(r => r.Id).ToListAsync();
        for (int i = 0; i < versions.Count; i++) versions[i].VersionNumber = i + 1;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Serves a stored PDF as a physical file response. Documents live under the uploads
    /// root (see <see cref="UploadStorage"/>) and are never mapped as static files — this
    /// method is the only path that exposes them, and it's only called after an ownership check.
    /// </summary>
    private IActionResult ServeFile(string storedPath, string originalName)
    {
        var fullPath = _uploads.Resolve(storedPath);
        if (!System.IO.File.Exists(fullPath)) return NotFound();
        return PhysicalFile(fullPath, "application/pdf", originalName);
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
