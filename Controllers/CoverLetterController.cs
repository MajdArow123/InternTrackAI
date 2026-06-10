using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;

namespace InternTrackAI.Controllers;

[Authorize]
public class CoverLetterController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly CoverLetterGeneratorService _generator;

    public CoverLetterController(ApplicationDbContext db, CoverLetterGeneratorService generator)
    {
        _db        = db;
        _generator = generator;
    }

    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ── GET /CoverLetter/Generate[?appId=N] ──────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Generate(int? appId)
    {
        var uid = UserId();

        var vm = new CoverLetterGeneratorViewModel
        {
            Applications = await _db.JobApplications
                .Where(a => a.UserId == uid)
                .OrderByDescending(a => a.DateApplied ?? a.Deadline ?? DateTime.MinValue)
                .ThenBy(a => a.CompanyName)
                .ToListAsync(),

            SavedLetters = await _db.GeneratedCoverLetters
                .Where(c => c.UserId == uid)
                .OrderByDescending(c => c.GeneratedAt)
                .ToListAsync(),

            SelectedApplicationId = appId,

            HasActiveResume = await _db.ResumeVersions
                .AnyAsync(r => r.UserId == uid && r.IsActive)
        };

        return View(vm);
    }

    // ── POST /CoverLetter/GenerateAjax  (AJAX, JSON body) ───────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateAjax([FromBody] GenerateRequest req)
    {
        var uid = UserId();

        // Load the selected job application
        JobApplication? app = null;
        if (req.ApplicationId.HasValue)
            app = await _db.JobApplications
                .FirstOrDefaultAsync(a => a.Id == req.ApplicationId && a.UserId == uid);

        // Extract text from the user's active resume
        var resumeText   = "";
        var activeResume = await _db.ResumeVersions
            .FirstOrDefaultAsync(r => r.UserId == uid && r.IsActive);

        if (activeResume != null && System.IO.File.Exists(activeResume.StoredPath))
        {
            try
            {
                await using var fs = System.IO.File.OpenRead(activeResume.StoredPath);
                resumeText = ResumeMatcherService.ExtractPdfText(fs);
            }
            catch { /* generate without resume text if extraction fails */ }
        }

        // Load profile data (name, skills, target roles)
        var profile     = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == uid);
        var fullName    = profile?.FullName ?? "";
        var skills      = "";
        var targetRoles = "";

        if (profile != null)
        {
            if (!string.IsNullOrEmpty(profile.SkillsJson))
            {
                var list = JsonSerializer.Deserialize<List<string>>(profile.SkillsJson);
                skills = string.Join(", ", list ?? []);
            }
            if (!string.IsNullOrEmpty(profile.TargetRolesJson))
            {
                var list = JsonSerializer.Deserialize<List<string>>(profile.TargetRolesJson);
                targetRoles = string.Join(", ", list ?? []);
            }
        }

        var company        = app?.CompanyName    ?? "the company";
        var role           = app?.RoleTitle      ?? "the position";
        var jobDescription = app?.JobDescription ?? "";

        var (success, content, error) = await _generator.GenerateAsync(
            company, role, jobDescription,
            resumeText, fullName, skills, targetRoles,
            req.ExtraNotes ?? "");

        if (!success)
            return Json(new { success = false, error });

        return Json(new { success = true, content, company, role, applicationId = req.ApplicationId });
    }

    // ── POST /CoverLetter/Save ───────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int? applicationId, string content,
        string? company, string? role)
    {
        var uid = UserId();
        if (string.IsNullOrWhiteSpace(content))
            return RedirectToAction(nameof(Generate));

        var maxVersion = await _db.GeneratedCoverLetters
            .Where(c => c.UserId == uid)
            .MaxAsync(c => (int?)c.VersionNumber) ?? 0;

        var letter = new GeneratedCoverLetter
        {
            UserId           = uid,
            JobApplicationId = applicationId,
            Content          = content.Trim(),
            CompanyName      = company,
            RoleTitle        = role,
            GeneratedAt      = DateTime.UtcNow,
            IsActive         = maxVersion == 0,   // first saved letter is auto-active
            VersionNumber    = maxVersion + 1
        };

        _db.GeneratedCoverLetters.Add(letter);
        await _db.SaveChangesAsync();

        TempData["SavedOk"] = $"Cover letter v{letter.VersionNumber} saved.";
        return RedirectToAction(nameof(Generate));
    }

    // ── POST /CoverLetter/Delete/{id} ────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var uid    = UserId();
        var letter = await _db.GeneratedCoverLetters
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == uid);

        if (letter != null)
        {
            bool wasActive = letter.IsActive;
            _db.GeneratedCoverLetters.Remove(letter);
            await _db.SaveChangesAsync();

            // Renumber and restore active flag
            var remaining = await _db.GeneratedCoverLetters
                .Where(c => c.UserId == uid)
                .OrderBy(c => c.Id)
                .ToListAsync();

            for (int i = 0; i < remaining.Count; i++)
                remaining[i].VersionNumber = i + 1;

            if (wasActive && remaining.Any())
                remaining[0].IsActive = true;

            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Generate));
    }

    // ── POST /CoverLetter/SetActive/{id} ─────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(int id)
    {
        var uid     = UserId();
        var letters = await _db.GeneratedCoverLetters
            .Where(c => c.UserId == uid)
            .ToListAsync();

        foreach (var l in letters)
            l.IsActive = l.Id == id;

        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Generate));
    }

    // ── GET /CoverLetter/Download/{id} ───────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Download(int id)
    {
        var uid    = UserId();
        var letter = await _db.GeneratedCoverLetters
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == uid);

        if (letter == null) return NotFound();

        var bytes    = System.Text.Encoding.UTF8.GetBytes(letter.Content);
        var filename = $"cover_letter_v{letter.VersionNumber}.txt";
        return File(bytes, "text/plain; charset=utf-8", filename);
    }
}

public record GenerateRequest(int? ApplicationId, string? ExtraNotes);
