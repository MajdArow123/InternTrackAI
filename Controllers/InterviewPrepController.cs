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

    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // GET /InterviewPrep/Prep/{appId}
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

    // POST /InterviewPrep/Generate  (AJAX)
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
