using System.Security.Claims;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;
using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Controllers;

/// <summary>
/// Manages the user's career portfolio: personal info, profile photo, skills/target-role tags,
/// resume version history (upload, activate, delete, download), AI resume
/// scoring, and the AI resume-match endpoint used by the Job Application create page.
/// </summary>
[Authorize]
public class ProfileController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly UploadStorage _uploads;
    private readonly ResumeScoreService _scorer;
    private readonly ResumeMatcherService _matcher;
    private readonly ProfileExtractorService _extractor;
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
        ProfileExtractorService extractor,
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
        _extractor = extractor;
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

        profile.FullName       = fullName.Trim();
        profile.DisplayName    = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
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

    /// <summary>
    /// Replaces the user's profile photo. Validates extension and size, deletes the previous
    /// photo file, and bumps <c>PhotoVersion</c> so the new image cache-busts in the UI (the
    /// stored filename is the user id, so the version number is the only thing that changes).
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadPhoto(IFormFile? photo)
    {
        if (photo == null || photo.Length == 0)
        {
            TempData["Error"] = "Please select a photo.";
            return RedirectToAction(nameof(Index));
        }

        var ext = Path.GetExtension(photo.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
        {
            TempData["Error"] = "Photos must be JPG, PNG, or WebP.";
            return RedirectToAction(nameof(Index));
        }

        if (photo.Length > 3 * 1024 * 1024)
        {
            TempData["Error"] = "Photo must be under 3 MB.";
            return RedirectToAction(nameof(Index));
        }

        var userId = UserId();
        var dir = _uploads.PhotosDirectory;

        // Delete old photo
        var profile = await GetOrCreateProfileAsync(userId);
        if (profile.PhotoFileName != null)
        {
            var old = Path.Combine(dir, profile.PhotoFileName);
            if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
        }

        var fileName = $"{userId}{ext}";
        await using var fs = System.IO.File.Create(Path.Combine(dir, fileName));
        await photo.CopyToAsync(fs);

        profile.PhotoFileName = fileName;
        profile.PhotoVersion++;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Photo updated.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/SaveSkills ─────────────────────────

    /// <summary>
    /// Auto-saves the tag-based skills list via AJAX, deduplicated and JSON-encoded into
    /// <c>UserProfile.SkillsJson</c>. Called immediately whenever a skill tag is added or removed
    /// on the Profile page, so there's no separate "Save" button or page reload.
    /// </summary>
    /// <param name="skillsJson">A JSON array of skill strings from the tag input widget.</param>
    /// <returns>JSON <c>{ success: true }</c> on save.</returns>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSkills(string? skillsJson)
    {
        var userId = UserId();
        var profile = await GetOrCreateProfileAsync(userId);
        profile.SkillsJson = NormalizeTagJson(skillsJson);
        await _db.SaveChangesAsync();
        return Json(new { success = true });
    }

    // ── POST /Profile/SaveTargetRoles ────────────────────

    /// <summary>
    /// Auto-saves the tag-based target-roles list via AJAX, deduplicated and JSON-encoded into
    /// <c>UserProfile.TargetRolesJson</c>. Called immediately whenever a role tag is added or
    /// removed on the Profile page (including from the searchable role combobox).
    /// </summary>
    /// <param name="targetRolesJson">A JSON array of role-name strings from the tag input widget.</param>
    /// <returns>JSON <c>{ success: true }</c> on save.</returns>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTargetRoles(string? targetRolesJson)
    {
        var userId = UserId();
        var profile = await GetOrCreateProfileAsync(userId);
        profile.TargetRolesJson = NormalizeTagJson(targetRolesJson);
        await _db.SaveChangesAsync();
        return Json(new { success = true });
    }

    // ── POST /Profile/AnalyzeResume (AJAX) ───────────────

    /// <summary>
    /// Extracts text from the user's active resume and asks <see cref="ProfileExtractorService"/>
    /// to pull out a full name, skills, and likely target roles, so the profile form can be
    /// auto-filled right after upload instead of the user retyping information already on their resume.
    /// This only returns the extracted data — saving it is a separate AJAX call from the client
    /// (the existing SaveInfo/SaveSkills/SaveTargetRoles endpoints) so the user can see and adjust
    /// the suggestions before they're persisted.
    /// </summary>
    /// <returns>
    /// JSON <c>{ success, hasResume, fullName, skills, targetRoles, error }</c>. <c>hasResume</c> is
    /// false if no active resume exists yet (nothing to analyze).
    /// </returns>
    [HttpPost, ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> AnalyzeResume()
    {
        var userId = UserId();
        var activeResume = await _db.ResumeVersions.FirstOrDefaultAsync(r => r.UserId == userId && r.IsActive);
        if (activeResume == null)
            return Json(new { success = false, hasResume = false });

        var filePath = _uploads.Resolve(activeResume.StoredPath);
        if (!System.IO.File.Exists(filePath))
            return Json(new { success = false, hasResume = false, error = "Resume file not found. Try uploading it again." });

        string resumeText;
        try
        {
            await using var fs = System.IO.File.OpenRead(filePath);
            resumeText = ResumeMatcherService.ExtractPdfText(fs);
        }
        catch
        {
            return Json(new { success = false, hasResume = true, error = "Could not read the PDF. Make sure it is a text-based (not scanned) PDF." });
        }

        if (string.IsNullOrWhiteSpace(resumeText) || resumeText.Length < 50)
            return Json(new { success = false, hasResume = true, error = "No readable text found in the PDF." });

        var (success, fullName, skills, targetRoles, error) = await _extractor.ExtractAsync(resumeText);
        if (!success)
            return Json(new { success = false, hasResume = true, error });

        return Json(new { success = true, hasResume = true, fullName, skills, targetRoles });
    }

    // ── POST /Profile/UploadResume ───────────────────────

    /// <summary>Uploads a new resume PDF as the next version (see <see cref="UploadResumeAsync"/> for validation rules).</summary>
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
            ResumeStats   = ResumeAnalyticsService.Build(resumes, apps).ByResumeId,
            Profile       = profile,
            Email         = user?.Email,
            Resumes       = resumes,
            Skills        = ParseTagJson(profile.SkillsJson),
            TargetRoles   = ParseTagJson(profile.TargetRolesJson),
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
        await using var fs = System.IO.File.Create(fullPath);
        await file.CopyToAsync(fs);

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
        TempData["Success"] = $"{label} v{await _db.ResumeVersions.CountAsync(r => r.UserId == userId)} uploaded.";
        return RedirectToAction(nameof(Index));
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

    /// <summary>Deserializes a JSON string array (skills or target roles), tolerating null/malformed input.</summary>
    private static List<string> ParseTagJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Trims, dedupes, and drops empty entries from a tag list before re-serializing it to JSON for storage.</summary>
    private static string? NormalizeTagJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            if (list == null || list.Count == 0) return null;
            return JsonSerializer.Serialize(list.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList());
        }
        catch { return null; }
    }

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
