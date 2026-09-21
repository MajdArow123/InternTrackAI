using System.Security.Claims;
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
/// The practice page: every question the user has, filtered by difficulty and category, with a
/// "Get more" that appends without a reload.
/// </summary>
/// <remarks>
/// Reads the same <see cref="PracticeQuestion"/> store as <see cref="InterviewPrepController"/> —
/// questions generated for an application show up here too. "Get more" returns a <b>rendered Razor
/// partial</b> rather than JSON, which is the pattern this app uses for dynamic content (CLAUDE.md
/// §8): the markup for a question card is written once, in the partial, and both the first page load
/// and every later append use it.
/// </remarks>
[Authorize]
public class PracticeController : Controller
{
    /// <summary>Questions per "Get more". Small on purpose: a wall of twenty is not practice.</summary>
    public const int BatchSize = 5;

    private readonly ApplicationDbContext _db;
    private readonly PracticeQuestionService _questions;
    private readonly PracticeAnswerService _answers;
    private readonly IUserContextBuilder _userContext;

    public PracticeController(ApplicationDbContext db, PracticeQuestionService questions,
                              PracticeAnswerService answers, IUserContextBuilder userContext)
    {
        _db = db;
        _questions = questions;
        _answers = answers;
        _userContext = userContext;
    }

    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ── GET /Practice ────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Index(string? difficulty, string? category, bool saved = false)
    {
        var userId = UserId();
        var selectedDifficulty = ParseDifficulty(difficulty);
        var selectedCategory   = ParseCategory(category);

        var query = _db.PracticeQuestions.AsNoTracking().Where(q => q.UserId == userId);
        if (selectedDifficulty is { } d) query = query.Where(q => q.Difficulty == d);
        if (selectedCategory is { } c)   query = query.Where(q => q.Category == c);
        if (saved)                       query = query.Where(q => q.IsSaved);

        var questions = await query.OrderByDescending(q => q.Id).ToListAsync(HttpContext.RequestAborted);

        return View(new PracticeViewModel
        {
            Groups     = await GroupAsync(userId, questions, HttpContext.RequestAborted),
            Progress   = await ProgressAsync(userId, HttpContext.RequestAborted),
            Difficulty = selectedDifficulty,
            Category   = selectedCategory,
            SavedOnly  = saved,
            TotalCount = await _db.PracticeQuestions.CountAsync(q => q.UserId == userId, HttpContext.RequestAborted)
        });
    }

    /// <summary>
    /// Splits the questions into one group per application, newest group first, with general practice
    /// last.
    /// </summary>
    /// <remarks>
    /// <b>Grouped and ordered by <c>ApplicationId</c>, an int, never by company name.</b> A text
    /// <c>GROUP BY</c> or <c>ORDER BY</c> takes the database's collation, which is <c>C</c> locally and
    /// <c>en_US.utf8</c> in production (CLAUDE.md §8) — the divergence that already shipped one wrong
    /// sort. The company and role are fetched as two strings for the header and play no part in the
    /// ordering. General practice sits last because it is the fallback bucket, not a posting.
    /// </remarks>
    private async Task<List<PracticeGroup>> GroupAsync(string userId, List<PracticeQuestion> questions, CancellationToken ct)
    {
        if (questions.Count == 0) return new();

        var appIds = questions.Where(q => q.ApplicationId is not null)
                              .Select(q => q.ApplicationId!.Value)
                              .Distinct()
                              .ToList();

        // Owner-scoped, so a question whose application was somehow not the user's contributes no label.
        var applications = appIds.Count == 0
            ? new Dictionary<int, (string Company, string Role)>()
            : (await _db.JobApplications.AsNoTracking()
                    .Where(a => a.UserId == userId && appIds.Contains(a.Id))
                    .Select(a => new { a.Id, a.CompanyName, a.RoleTitle })
                    .ToListAsync(ct))
                .ToDictionary(a => a.Id, a => (Company: a.CompanyName, Role: a.RoleTitle));

        var groups = questions
            .GroupBy(q => q.ApplicationId)
            .Select(g => new
            {
                ApplicationId = g.Key,
                Newest        = g.Max(q => q.Id),
                Questions     = g.ToList()
            })
            .OrderByDescending(g => g.ApplicationId is null ? 0 : 1)   // general practice last
            .ThenByDescending(g => g.Newest)
            .Select(g =>
            {
                applications.TryGetValue(g.ApplicationId ?? 0, out var app);
                return new PracticeGroup(g.ApplicationId, app.Company, app.Role, g.Questions);
            })
            .ToList();

        return groups.Count == 1
            ? new List<PracticeGroup> { groups[0] with { IsOnlyGroup = true } }
            : groups;
    }

    /// <summary>
    /// The progress card. One query, five narrow columns, aggregated in C# — see
    /// <see cref="PracticeProgress"/> for why it is not SQL aggregates.
    /// </summary>
    /// <remarks>
    /// Deliberately unfiltered: this is the user's progress, not a summary of whatever they are
    /// currently looking at, and a card whose numbers moved when a filter changed would read as a bug.
    /// </remarks>
    private async Task<PracticeProgress> ProgressAsync(string userId, CancellationToken ct)
    {
        var rows = await _db.PracticeQuestions.AsNoTracking()
            .Where(q => q.UserId == userId)
            .Select(q => new PracticeProgressRow(q.Id, q.Difficulty, q.Score, q.Topic, q.AnsweredAt))
            .ToListAsync(ct);

        return PracticeProgress.Build(rows);
    }

    // ── POST /Practice/GenerateMore ──────────────────────

    /// <summary>
    /// Generates the next batch and returns the rendered cards, which the client appends.
    /// </summary>
    /// <remarks>
    /// The filters above the list are <b>also</b> the generation parameters, so this takes the same
    /// difficulty and category the user is looking at. An unset filter has to become something, and it
    /// becomes Medium/Technical rather than "any" — the generator needs one of each to write against.
    /// </remarks>
    [HttpPost, ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> GenerateMore(string? difficulty, string? category, int? applicationId)
    {
        var userId = UserId();

        // Owner-scoped before anything is generated: a foreign id is 404, never silently ignored.
        if (applicationId is { } appId &&
            !await _db.JobApplications.AnyAsync(a => a.Id == appId && a.UserId == userId))
            return NotFound(new { success = false, error = "Application not found." });

        var result = await _questions.GenerateAsync(
            userId,
            ParseDifficulty(difficulty) ?? PracticeDifficulty.Medium,
            ParseCategory(category) ?? QuestionCategory.Technical,
            BatchSize,
            applicationId,
            await _userContext.BuildAsync(userId, HttpContext.RequestAborted),
            HttpContext.RequestAborted);

        if (!result.Success)
            return Json(new { success = false, error = result.Error });

        return Json(new
        {
            success = true,
            added   = result.Questions.Count,
            note    = result.Note,
            html    = await this.RenderPartialAsync("_PracticeQuestions", result.Questions)
        });
    }

    // ── POST /Practice/SubmitAnswer ──────────────────────

    /// <summary>
    /// Scores the user's answer to one question and returns that card re-rendered in its answered state.
    /// </summary>
    /// <remarks>
    /// Returns HTML rather than the feedback as JSON for the same reason "Get more" does: the answered
    /// card's markup then exists exactly once, in <c>_PracticeQuestion.cshtml</c>, and a first page load
    /// and a fresh submission cannot drift apart. The client swaps the card it submitted from.
    /// </remarks>
    [HttpPost, ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> SubmitAnswer(int questionId, string? answer)
    {
        var userId = UserId();

        // Length is checked before the row is even loaded: a too-short answer must cost nothing.
        if (PracticeAnswerService.Validate(answer) is { } invalid)
            return Json(new { success = false, error = invalid });

        // Tracked, because this is the write path. A foreign id is 404, per the app's convention.
        var question = await _db.PracticeQuestions
            .FirstOrDefaultAsync(q => q.Id == questionId && q.UserId == userId, HttpContext.RequestAborted);
        if (question is null)
            return NotFound(new { success = false, error = "Question not found." });

        var result = await _answers.SubmitAsync(userId, question, answer!, HttpContext.RequestAborted);

        if (!result.Success)
            return Json(new { success = false, error = result.Error });

        // The progress card is re-rendered and returned with the card. Answering changes every number
        // on it, and leaving it stale until a reload is worse than leaving it obviously broken: it
        // keeps showing 0/5 in a card that reads as authoritative.
        return Json(new
        {
            success  = true,
            score    = result.Feedback!.Score,
            html     = await this.RenderPartialAsync("_PracticeQuestion", question),
            progress = await this.RenderPartialAsync("_PracticeProgress",
                           await ProgressAsync(userId, HttpContext.RequestAborted))
        });
    }

    // ── POST /Practice/ToggleSaved ───────────────────────

    /// <summary>Stars or unstars one question. Returns the state it landed in, not the one asked for.</summary>
    /// <remarks>
    /// <b>No rate limit</b>, because it makes no model call — the same reason
    /// <c>JobApplicationsController.KeywordCoverage</c> stays off the <c>"ai"</c> policy. The response
    /// carries the resulting state rather than echoing the request so a double-click cannot leave the
    /// star and the row disagreeing.
    /// </remarks>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleSaved(int questionId)
    {
        var userId = UserId();

        var question = await _db.PracticeQuestions
            .FirstOrDefaultAsync(q => q.Id == questionId && q.UserId == userId, HttpContext.RequestAborted);
        if (question is null)
            return NotFound(new { success = false, error = "Question not found." });

        question.IsSaved = !question.IsSaved;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        return Json(new { success = true, saved = question.IsSaved });
    }

    /// <summary>Blank or unrecognised means "no filter", never a throw — the values come from a query string.</summary>
    private static PracticeDifficulty? ParseDifficulty(string? value) =>
        Enum.TryParse<PracticeDifficulty>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private static QuestionCategory? ParseCategory(string? value) =>
        Enum.TryParse<QuestionCategory>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
}
