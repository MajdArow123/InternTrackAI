using System.Security.Claims;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Helpers;
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
/// The interview prep page for one application: generates posting-grounded questions and stores them as
/// <see cref="PracticeQuestion"/> rows. The page renders them with the practice page's own card, so
/// answering goes through <c>/Practice/SubmitAnswer</c> by question id — there is no answer endpoint here.
/// </summary>
[Authorize]
public class InterviewPrepController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly InterviewPrepService _service;
    private readonly ResumeTextService _resumeText;
    private readonly IUserContextBuilder _userContext;
    private readonly ILogger<InterviewPrepController> _logger;

    public InterviewPrepController(ApplicationDbContext db, InterviewPrepService service,
                                   ResumeTextService resumeText, IUserContextBuilder userContext,
                                   ILogger<InterviewPrepController> logger)
    {
        _db      = db;
        _service = service;
        _resumeText = resumeText;
        _userContext = userContext;
        _logger = logger;
    }

    /// <summary>Resolves the current signed-in user's id from the auth claims.</summary>
    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

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
    /// JSON <c>{ success, added, html }</c> — the newly stored questions rendered by
    /// <c>_PrepQuestionGroups</c> (empty when nothing new was stored) — or <c>{ success: false, error }</c>
    /// if the application can't be found or the AI call fails.
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

        // Only what was stored, rendered with the practice card and grouped by category. The page merges
        // it into the sections it already shows; an empty result means "nothing new", not "no questions",
        // which is the distinction the old JSON-and-replace client could not make.
        return Json(new
        {
            success = true,
            added   = stored.Count,
            html    = stored.Count == 0 ? "" : await this.RenderPartialAsync("_PrepQuestionGroups", stored)
        });
    }

    /// <summary>
    /// Turns freshly generated questions into <see cref="PracticeQuestion"/> rows, dropping any the
    /// user already has. Returns what was actually stored, newest generation only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deduped by hash only — the topic is stored but compared against nothing, deliberately.</b> Prep
    /// topics were measured (2026-09-24, three live calls, two postings) to come back category-level:
    /// "Collaboration", "Automated testing", "Docker and Kubernetes". <see cref="TopicKey"/> treats a
    /// short topic as colliding with every longer topic containing its words, so comparing topics in
    /// either direction over-blocks: a prep "automated testing" would suppress practice's "automated
    /// testing for payment flows", and that narrow practice topic would in turn suppress the prep
    /// question. The topic is still worth storing — the progress card's weakest-topic grouping reads it,
    /// and breadth does no harm there — and the row is marked <see cref="QuestionSource.InterviewPrep"/>
    /// so <see cref="PracticeQuestionService.ExclusionsAsync"/> can leave it out. See
    /// <see cref="QuestionSource"/> for the full record.
    /// </para>
    /// <para>
    /// The hash filter catches what it always did: the batch against itself (a model asked for ten
    /// questions will sometimes give the same one twice) and against every hash the user already has,
    /// from either page.
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
        int hashDrops = 0, blankDrops = 0;

        foreach (var g in generated)
        {
            var hash = QuestionHash.Of(g.Question);
            if (hash.Length == 0) { blankDrops++; continue; }
            if (!seen.Add(hash)) { hashDrops++; continue; }

            rows.Add(new PracticeQuestion
            {
                UserId        = uid,
                ApplicationId = appId,
                Prompt        = g.Question,
                Category      = g.Category,
                Topic         = g.Topic,
                ModelHint     = g.Tip,
                PromptHash    = hash,
                // Written out rather than left to the column default: prep questions are first-round
                // interview questions, which is PracticePrompt.DifficultyRule's definition of Medium.
                Difficulty    = PracticeDifficulty.Medium,
                Source        = QuestionSource.InterviewPrep,
                CreatedAt     = DateTime.UtcNow
            });
        }

        var stored = await StoreAsync(rows);

        // The same one-line summary practice generation writes, so "I regenerated and nothing new
        // appeared" can be answered from the log: an exhausted application and a broken call look
        // identical on the page.
        _logger.LogInformation(
            "Interview prep generation for application {AppId}: model returned {Returned}, "
            + "dropped {HashDrops} same-question + {BlankDrops} blank, stored {Stored}.",
            appId, generated.Count, hashDrops, blankDrops, stored.Count);

        return stored;
    }

    /// <summary>Saves the batch, falling back to row by row if the unique index fires on a race.</summary>
    private async Task<List<PracticeQuestion>> StoreAsync(List<PracticeQuestion> rows)
    {
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
}

public record GeneratePrepRequest(int AppId);

