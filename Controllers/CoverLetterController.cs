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

/// <summary>
/// Drives the AI cover letter feature: generating a letter from a tracked application + the
/// user's resume/profile context, and managing the saved-letter version history (save, delete,
/// set active, download).
/// </summary>
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

    /// <summary>Resolves the current signed-in user's id from the auth claims.</summary>
    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ── GET /CoverLetter/Generate[?appId=N] ──────────────────────────────────

    /// <summary>
    /// Renders the cover letter generator page: the user's applications (to pick one as context),
    /// their saved letter history, and whether they have an active resume on file.
    /// </summary>
    /// <param name="appId">Optional application id to preselect (e.g. linked from the Applications list "Cover Letter" button).</param>
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

    /// <summary>
    /// Builds the AI prompt context (selected application's job description, extracted resume
    /// text, profile name/skills/target roles, optional user notes) and calls
    /// <see cref="CoverLetterGeneratorService"/> to produce a draft letter. Called via fetch() from
    /// the Generate page, so it keeps <c>[ValidateAntiForgeryToken]</c> (the token is sent
    /// explicitly by the page's JS) since this endpoint triggers a billed OpenAI call.
    /// </summary>
    /// <param name="req">JSON body with the selected application id and optional extra notes to guide tone/content.</param>
    /// <returns>JSON with the generated content on success, or an error message on failure.</returns>
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

    // ── POST /CoverLetter/ImproveAjax  (AJAX, JSON body) ────────────────────

    /// <summary>
    /// Rewrites the letter currently in the editor per the applicant's instructions (tone, length,
    /// clarity, etc.) without inventing new resume facts. Kept separate from
    /// <see cref="GenerateAjax"/> since it edits existing text rather than drafting from scratch.
    /// </summary>
    /// <param name="req">JSON body with the current draft text, optional application id for company/role
    /// context, and free-text improvement instructions.</param>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImproveAjax([FromBody] ImproveRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Content))
            return Json(new { success = false, error = "Nothing to improve — the editor is empty." });

        var uid = UserId();

        JobApplication? app = null;
        if (req.ApplicationId.HasValue)
            app = await _db.JobApplications
                .FirstOrDefaultAsync(a => a.Id == req.ApplicationId && a.UserId == uid);

        var company = app?.CompanyName ?? req.Company ?? "";
        var role    = app?.RoleTitle   ?? req.Role    ?? "";

        var (success, content, error) = await _generator.ImproveAsync(
            req.Content, company, role, req.Instructions ?? "");

        if (!success)
            return Json(new { success = false, error });

        return Json(new { success = true, content });
    }

    // ── POST /CoverLetter/Save ───────────────────────────────────────────────

    /// <summary>
    /// Saves an edited/generated letter to the user's version history. The new version number is
    /// always max+1 (never reused), and the very first saved letter is automatically marked active.
    /// </summary>
    /// <param name="applicationId">Optional id of the application this letter was generated for (for snapshotting company/role).</param>
    /// <param name="content">The (possibly user-edited) letter text.</param>
    /// <param name="company">Snapshot of the company name at save time, shown in the history list.</param>
    /// <param name="role">Snapshot of the role title at save time, shown in the history list.</param>
    /// <returns>Redirect back to Generate; no-ops (no redirect change, just skips saving) if content is blank.</returns>
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

        TempData["Toast"] = $"success|Cover letter v{letter.VersionNumber} saved.";
        return RedirectToAction(nameof(Generate));
    }

    // ── POST /CoverLetter/Delete/{id} ────────────────────────────────────────

    /// <summary>
    /// Deletes a saved letter, then renumbers the remaining letters sequentially (1..N) so version
    /// numbers stay contiguous, and promotes the oldest remaining letter to active if the deleted
    /// one was active.
    /// </summary>
    /// <param name="id">The letter id to delete; ownership is checked against the current user.</param>
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

    /// <summary>Marks one saved letter as the active version and unmarks all others for this user.</summary>
    /// <param name="id">The letter id to activate; ownership is checked against the current user.</param>
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

    /// <summary>Streams a saved letter back as a plain-text file download.</summary>
    /// <param name="id">The letter id to download; ownership is checked against the current user.</param>
    /// <returns>A <c>.txt</c> file response, or 404 if the letter doesn't exist or isn't owned by the user.</returns>
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

public record ImproveRequest(string Content, int? ApplicationId, string? Company, string? Role, string? Instructions);
