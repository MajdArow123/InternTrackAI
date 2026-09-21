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
    private readonly IUserContextBuilder _userContext;

    public PracticeController(ApplicationDbContext db, PracticeQuestionService questions, IUserContextBuilder userContext)
    {
        _db = db;
        _questions = questions;
        _userContext = userContext;
    }

    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ── GET /Practice ────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Index(string? difficulty, string? category)
    {
        var userId = UserId();
        var selectedDifficulty = ParseDifficulty(difficulty);
        var selectedCategory   = ParseCategory(category);

        var query = _db.PracticeQuestions.AsNoTracking().Where(q => q.UserId == userId);
        if (selectedDifficulty is { } d) query = query.Where(q => q.Difficulty == d);
        if (selectedCategory is { } c)   query = query.Where(q => q.Category == c);

        return View(new PracticeViewModel
        {
            Questions  = await query.OrderByDescending(q => q.Id).ToListAsync(),
            Difficulty = selectedDifficulty,
            Category   = selectedCategory,
            TotalCount = await _db.PracticeQuestions.CountAsync(q => q.UserId == userId)
        });
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

    /// <summary>Blank or unrecognised means "no filter", never a throw — the values come from a query string.</summary>
    private static PracticeDifficulty? ParseDifficulty(string? value) =>
        Enum.TryParse<PracticeDifficulty>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private static QuestionCategory? ParseCategory(string? value) =>
        Enum.TryParse<QuestionCategory>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
}
