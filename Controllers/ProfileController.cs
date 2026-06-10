using System.Security.Claims;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Controllers;

[Authorize]
public class ProfileController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly IWebHostEnvironment _env;
    private readonly ResumeScoreService _scorer;
    private readonly ResumeMatcherService _matcher;

    public ProfileController(
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager,
        IWebHostEnvironment env,
        ResumeScoreService scorer,
        ResumeMatcherService matcher)
    {
        _db = db;
        _userManager = userManager;
        _env = env;
        _scorer = scorer;
        _matcher = matcher;
    }

    // ── GET /Profile ────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var vm = await BuildViewModelAsync();
        return View(vm);
    }

    // ── POST /Profile/SaveInfo ───────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveInfo(string? fullName, int? age, string? country, string? phoneNumber)
    {
        var userId = UserId();
        var profile = await GetOrCreateProfileAsync(userId);

        profile.FullName    = fullName?.Trim();
        profile.Age         = age;
        profile.Country     = country?.Trim();
        profile.PhoneNumber = phoneNumber?.Trim();

        await _db.SaveChangesAsync();
        TempData["Success"] = "Profile saved.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/UploadPhoto ────────────────────────

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
        var dir = Path.Combine(_env.WebRootPath, "uploads", "photos");
        Directory.CreateDirectory(dir);

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

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSkills(string? skillsJson)
    {
        var userId = UserId();
        var profile = await GetOrCreateProfileAsync(userId);
        profile.SkillsJson = NormalizeTagJson(skillsJson);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Skills saved.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/SaveTargetRoles ────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTargetRoles(string? targetRolesJson)
    {
        var userId = UserId();
        var profile = await GetOrCreateProfileAsync(userId);
        profile.TargetRolesJson = NormalizeTagJson(targetRolesJson);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Target roles saved.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/UploadResume ───────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadResume(IFormFile? resume)
        => await UploadDocumentAsync(resume, isResume: true);

    // ── POST /Profile/UploadCoverLetter ──────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadCoverLetter(IFormFile? coverLetter)
        => await UploadDocumentAsync(coverLetter, isResume: false);

    // ── POST /Profile/SetActiveResume ────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActiveResume(int id)
    {
        var userId = UserId();
        var versions = await _db.ResumeVersions.Where(r => r.UserId == userId).ToListAsync();
        foreach (var v in versions) v.IsActive = v.Id == id;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Active resume updated.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/SetActiveCoverLetter ───────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActiveCoverLetter(int id)
    {
        var userId = UserId();
        var versions = await _db.CoverLetterVersions.Where(c => c.UserId == userId).ToListAsync();
        foreach (var v in versions) v.IsActive = v.Id == id;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Active cover letter updated.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/DeleteResume ───────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteResume(int id)
    {
        var userId = UserId();
        var resume = await _db.ResumeVersions.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (resume != null)
        {
            DeleteFile(resume.StoredPath);
            _db.ResumeVersions.Remove(resume);
            await _db.SaveChangesAsync();
            // Renumber remaining versions
            await RenumberVersionsAsync(userId, isResume: true);
        }
        TempData["Success"] = "Resume deleted.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/DeleteCoverLetter ──────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCoverLetter(int id)
    {
        var userId = UserId();
        var cl = await _db.CoverLetterVersions.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (cl != null)
        {
            DeleteFile(cl.StoredPath);
            _db.CoverLetterVersions.Remove(cl);
            await _db.SaveChangesAsync();
            await RenumberVersionsAsync(userId, isResume: false);
        }
        TempData["Success"] = "Cover letter deleted.";
        return RedirectToAction(nameof(Index));
    }

    // ── GET /Profile/DownloadResume/{id} ─────────────────

    [HttpGet]
    public async Task<IActionResult> DownloadResume(int id)
    {
        var userId = UserId();
        var resume = await _db.ResumeVersions.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (resume == null) return NotFound();
        return ServeFile(resume.StoredPath, resume.OriginalFileName);
    }

    // ── GET /Profile/DownloadCoverLetter/{id} ────────────

    [HttpGet]
    public async Task<IActionResult> DownloadCoverLetter(int id)
    {
        var userId = UserId();
        var cl = await _db.CoverLetterVersions.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (cl == null) return NotFound();
        return ServeFile(cl.StoredPath, cl.OriginalFileName);
    }

    // ── POST /Profile/ScoreResume ────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
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

        var filePath = Path.Combine(_env.ContentRootPath, activeResume.StoredPath);
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

    [HttpPost, ValidateAntiForgeryToken]
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

        var filePath = Path.Combine(_env.ContentRootPath, activeResume.StoredPath);
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

    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

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

        var coverLetters = await _db.CoverLetterVersions
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.VersionNumber)
            .ToListAsync();

        var apps = await _db.JobApplications
            .Where(a => a.UserId == userId)
            .ToListAsync();

        var statusCounts = Enum.GetValues<ApplicationStatus>()
            .ToDictionary(s => s, s => apps.Count(a => a.Status == s));

        int offers = statusCounts.GetValueOrDefault(ApplicationStatus.Offer);
        double successRate = apps.Count > 0 ? Math.Round(offers * 100.0 / apps.Count, 1) : 0;

        return new ProfileViewModel
        {
            Profile       = profile,
            Email         = user?.Email,
            Resumes       = resumes,
            CoverLetters  = coverLetters,
            Skills        = ParseTagJson(profile.SkillsJson),
            TargetRoles   = ParseTagJson(profile.TargetRolesJson),
            TotalApplications = apps.Count,
            StatusCounts  = statusCounts,
            SuccessRate   = successRate
        };
    }

    private async Task<IActionResult> UploadDocumentAsync(IFormFile? file, bool isResume)
    {
        var label = isResume ? "Resume" : "Cover letter";

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

        var userId = UserId();
        var subDir = isResume ? "resumes" : "coverletters";
        var dir = Path.Combine(_env.ContentRootPath, "uploads", subDir, userId);
        Directory.CreateDirectory(dir);

        var stored = $"{Guid.NewGuid():N}.pdf";
        var fullPath = Path.Combine(dir, stored);
        await using var fs = System.IO.File.Create(fullPath);
        await file.CopyToAsync(fs);

        var relativePath = Path.Combine("uploads", subDir, userId, stored);

        if (isResume)
        {
            int nextVersion = (await _db.ResumeVersions.Where(r => r.UserId == userId).MaxAsync(r => (int?)r.VersionNumber) ?? 0) + 1;
            bool firstOne = nextVersion == 1;
            _db.ResumeVersions.Add(new ResumeVersion
            {
                UserId           = userId,
                VersionNumber    = nextVersion,
                OriginalFileName = file.FileName,
                StoredPath       = relativePath,
                FileSize         = file.Length,
                IsActive         = firstOne
            });
        }
        else
        {
            int nextVersion = (await _db.CoverLetterVersions.Where(c => c.UserId == userId).MaxAsync(c => (int?)c.VersionNumber) ?? 0) + 1;
            bool firstOne = nextVersion == 1;
            _db.CoverLetterVersions.Add(new CoverLetterVersion
            {
                UserId           = userId,
                VersionNumber    = nextVersion,
                OriginalFileName = file.FileName,
                StoredPath       = relativePath,
                FileSize         = file.Length,
                IsActive         = firstOne
            });
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = $"{label} v{(isResume ? await _db.ResumeVersions.CountAsync(r => r.UserId == userId) : await _db.CoverLetterVersions.CountAsync(c => c.UserId == userId))} uploaded.";
        return RedirectToAction(nameof(Index));
    }

    private async Task RenumberVersionsAsync(string userId, bool isResume)
    {
        if (isResume)
        {
            var versions = await _db.ResumeVersions.Where(r => r.UserId == userId).OrderBy(r => r.Id).ToListAsync();
            for (int i = 0; i < versions.Count; i++) versions[i].VersionNumber = i + 1;
        }
        else
        {
            var versions = await _db.CoverLetterVersions.Where(c => c.UserId == userId).OrderBy(c => c.Id).ToListAsync();
            for (int i = 0; i < versions.Count; i++) versions[i].VersionNumber = i + 1;
        }
        await _db.SaveChangesAsync();
    }

    private IActionResult ServeFile(string storedPath, string originalName)
    {
        var fullPath = Path.Combine(_env.ContentRootPath, storedPath);
        if (!System.IO.File.Exists(fullPath)) return NotFound();
        return PhysicalFile(fullPath, "application/pdf", originalName);
    }

    private void DeleteFile(string storedPath)
    {
        var fullPath = Path.Combine(_env.ContentRootPath, storedPath);
        if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
    }

    private static List<string> ParseTagJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

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
}

public record AutoMatchRequest(string JobDescription);
