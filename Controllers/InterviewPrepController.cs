using System.Security.Claims;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Controllers;

/// <summary>
/// Generates and stores AI interview-prep question sets for a specific job application —
/// one session per application, regenerable on demand from the job description, the user's
/// active resume, and their profile skills.
/// </summary>
[Authorize]
public class InterviewPrepController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly InterviewPrepService _service;

    public InterviewPrepController(ApplicationDbContext db, InterviewPrepService service)
    {
        _db      = db;
        _service = service;
    }

    /// <summary>Resolves the current signed-in user's id from the auth claims.</summary>
    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // Tolerates property-name casing mismatches when re-reading previously stored question JSON.
    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Renders the interview prep page for a job application, loading any previously generated
    /// question session if one exists (the page's "Generate" button calls <see cref="Generate"/>
    /// via AJAX to create or refresh it).
    /// </summary>
    /// <param name="appId">The job application id; ownership is checked against the current user.</param>
    /// <returns>The Prep view, or 404 if the application doesn't exist or isn't owned by the user.</returns>
    [HttpGet]
    public async Task<IActionResult> Prep(int appId)
    {
        var uid = UserId();
        var app = await _db.JobApplications
            .FirstOrDefaultAsync(a => a.Id == appId && a.UserId == uid);
        if (app is null) return NotFound();

        var session = await _db.InterviewPrepSessions
            .FirstOrDefaultAsync(s => s.JobApplicationId == appId && s.UserId == uid);

        List<InterviewQuestion> questions = new();
        if (session is not null)
        {
            try { questions = JsonSerializer.Deserialize<List<InterviewQuestion>>(session.QuestionsJson, _readOptions) ?? new(); }
            catch { /* malformed JSON — show empty */ }
        }

        var vm = new InterviewPrepViewModel
        {
            Application = app,
            Session     = session,
            Questions   = questions
        };

        return View(vm);
    }

    /// <summary>
    /// Generates a fresh set of AI interview questions for an application — pulling job context
    /// from the stored job description, the user's active resume (PDF text extracted on the fly),
    /// and their profile skills — and upserts the result as the application's prep session.
    /// </summary>
    /// <param name="req">The application id to generate questions for.</param>
    /// <returns>
    /// JSON <c>{ success, questions }</c> on success, or <c>{ success: false, error }</c> if the
    /// application can't be found or the AI call fails.
    /// </returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate([FromBody] GeneratePrepRequest req)
    {
        var uid = UserId();
        var app = await _db.JobApplications
            .FirstOrDefaultAsync(a => a.Id == req.AppId && a.UserId == uid);
        if (app is null)
            return Json(new { success = false, error = "Application not found." });

        // Resume text
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
            catch { }
        }

        // Skills
        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == uid);
        var skills  = "";
        if (!string.IsNullOrEmpty(profile?.SkillsJson))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(profile.SkillsJson);
                skills = string.Join(", ", list ?? new());
            }
            catch { }
        }

        var (success, questions, error) = await _service.GenerateAsync(
            app.CompanyName, app.RoleTitle,
            app.JobDescription ?? "",
            resumeText, skills);

        if (!success)
            return Json(new { success = false, error });

        var questionsJson = JsonSerializer.Serialize(questions);

        var existing = await _db.InterviewPrepSessions
            .FirstOrDefaultAsync(s => s.JobApplicationId == req.AppId && s.UserId == uid);

        if (existing is not null)
        {
            existing.QuestionsJson = questionsJson;
            existing.GeneratedAt   = DateTime.UtcNow;
        }
        else
        {
            _db.InterviewPrepSessions.Add(new InterviewPrepSession
            {
                UserId           = uid,
                JobApplicationId = req.AppId,
                QuestionsJson    = questionsJson,
                GeneratedAt      = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();

        return Json(new { success = true, questions });
    }
}

public record GeneratePrepRequest(int AppId);
