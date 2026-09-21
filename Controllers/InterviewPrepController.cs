using System.Security.Claims;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    private readonly ResumeTextService _resumeText;
    private readonly IUserContextBuilder _userContext;
    private readonly ILogger<InterviewPrepController> _logger;

    public InterviewPrepController(ApplicationDbContext db, InterviewPrepService service, ResumeTextService resumeText,
                                   IUserContextBuilder userContext, ILogger<InterviewPrepController> logger)
    {
        _db      = db;
        _service = service;
        _resumeText = resumeText;
        _userContext = userContext;
        _logger = logger;
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

        // One row per question now, filtered to this application, rather than a JSON blob per session.
        // Oldest first so regenerating appends below what is already there instead of reshuffling it.
        var questions = await _db.PracticeQuestions.AsNoTracking()
            .Where(q => q.ApplicationId == appId && q.UserId == uid)
            .OrderBy(q => q.Id)
            .ToListAsync();

        return View(new InterviewPrepViewModel { Application = app, Questions = questions });
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
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> Generate([FromBody] GeneratePrepRequest req)
    {
        var uid = UserId();
        var app = await _db.JobApplications
            .FirstOrDefaultAsync(a => a.Id == req.AppId && a.UserId == uid);
        if (app is null)
            return NotFound(new { success = false, error = "Application not found." });

        // Best effort: prep without resume text rather than fail if there is none to read.
        var resumeText = (await _resumeText.GetActiveAsync(uid)).Text ?? "";

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
            resumeText, skills,
            await _userContext.BuildAsync(uid, HttpContext.RequestAborted));

        if (!success)
            return Json(new { success = false, error });

        var stored = await SaveNewAsync(uid, req.AppId, questions);

        // The wire shape is unchanged — category as its display string, question, tip — because the
        // Prep page's script renders straight from it. Storing rows changed nothing the client sees.
        return Json(new
        {
            success   = true,
            questions = stored.Select(q => new
            {
                category = QuestionCategories.Display(q.Category),
                question = q.Prompt,
                tip      = q.ModelHint
            })
        });
    }

    /// <summary>
    /// Turns freshly generated questions into <see cref="PracticeQuestion"/> rows, dropping any the
    /// user already has. Returns what was actually stored, newest generation only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two filters, because they catch different things: the batch is deduped against itself (a model
    /// asked for ten questions will sometimes give the same one twice) and against the hashes already
    /// stored for this user (regenerating on the same application used to produce the same set again).
    /// </para>
    /// <para>
    /// The unique index is the real guarantee and it can still fire on a race between two generations.
    /// Rather than let that 500 a request whose questions are perfectly good, a failed batch is retried
    /// row by row and the losers are dropped — the user gets fewer questions, never an error.
    /// </para>
    /// </remarks>
    private async Task<List<PracticeQuestion>> SaveNewAsync(string uid, int appId, List<GeneratedQuestion> generated)
    {
        var existing = await _db.PracticeQuestions
            .Where(q => q.UserId == uid)
            .Select(q => q.PromptHash)
            .ToListAsync();

        var seen = new HashSet<string>(existing, StringComparer.Ordinal);
        var rows = new List<PracticeQuestion>();

        foreach (var g in generated)
        {
            var hash = QuestionHash.Of(g.Question);
            if (hash.Length == 0 || !seen.Add(hash)) continue;

            rows.Add(new PracticeQuestion
            {
                UserId        = uid,
                ApplicationId = appId,
                Prompt        = g.Question,
                Category      = g.Category,
                ModelHint     = g.Tip,
                PromptHash    = hash,
                CreatedAt     = DateTime.UtcNow
                // Difficulty and Topic keep their defaults until the Step 2 generator supplies them.
            });
        }

        if (rows.Count == 0) return rows;

        _db.PracticeQuestions.AddRange(rows);
        try
        {
            await _db.SaveChangesAsync();
            return rows;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            _logger.LogInformation("Practice question batch hit the unique index; retrying row by row.");
        }

        var saved = new List<PracticeQuestion>();
        foreach (var row in rows)
        {
            _db.PracticeQuestions.Add(row);
            try
            {
                await _db.SaveChangesAsync();
                saved.Add(row);
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();   // somebody else stored this question first
            }
        }

        return saved;
    }

    /// <summary>
    /// Critiques the candidate's typed practice answer to a single interview question, acting as
    /// an AI coach. Stateless — feedback is not persisted, since this is meant for in-the-moment
    /// practice rather than a saved history.
    /// </summary>
    /// <param name="req">The application id (for company/role context), the question being
    /// answered, and the candidate's answer text.</param>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> CritiqueAnswer([FromBody] CritiqueAnswerRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Answer))
            return Json(new { success = false, error = "Type an answer first." });

        var uid = UserId();
        var app = await _db.JobApplications
            .FirstOrDefaultAsync(a => a.Id == req.AppId && a.UserId == uid);
        if (app is null)
            return NotFound(new { success = false, error = "Application not found." });

        var (success, feedback, error) = await _service.CritiqueAnswerAsync(
            req.Question, req.Answer, app.RoleTitle, app.CompanyName,
            await _userContext.BuildAsync(uid, HttpContext.RequestAborted));

        if (!success)
            return Json(new { success = false, error });

        return Json(new { success = true, feedback });
    }
}

public record GeneratePrepRequest(int AppId);

public record CritiqueAnswerRequest(int AppId, string Question, string Answer);
