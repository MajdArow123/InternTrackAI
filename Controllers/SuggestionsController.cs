using System.Security.Claims;
using InternTrackAI.Services;
using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InternTrackAI.Controllers;

/// <summary>
/// Accept / Dismiss for inbox status suggestions, called by <c>suggestions.js</c> from the dashboard
/// card and the drawer. Both answer JSON and are owner-scoped: someone else's suggestion id (or one
/// that was already resolved) is a 404.
/// </summary>
[Authorize]
public class SuggestionsController : Controller
{
    private readonly SuggestionService _suggestions;
    private readonly UserClockProvider _clocks;

    public SuggestionsController(SuggestionService suggestions, UserClockProvider clocks)
    {
        _suggestions = suggestions;
        _clocks = clocks;
    }

    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Applies the suggested status (and interview time), marks it Accepted, notes it on the application.</summary>
    [HttpPost("Suggestions/{id:int}/accept"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept(int id) => Respond(await _suggestions.AcceptAsync(UserId(), id));

    /// <summary>Marks the suggestion Dismissed; the application is not touched.</summary>
    [HttpPost("Suggestions/{id:int}/dismiss"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Dismiss(int id) => Respond(await _suggestions.DismissAsync(UserId(), id));

    private IActionResult Respond(SuggestionOutcome? outcome)
    {
        if (outcome is null) return NotFound(new { success = false, error = "Suggestion not found." });

        var clock = _clocks.GetAsync().GetAwaiter().GetResult();
        return Ok(new
        {
            success       = true,
            id            = outcome.Id,
            applicationId = outcome.ApplicationId,
            state         = outcome.State.ToString(),
            accepted      = outcome.State == Models.Enums.SuggestionState.Accepted,
            status        = (int)outcome.ApplicationStatus,
            statusName    = outcome.ApplicationStatus.ToString(),
            interviewAt   = clock.LocalDateTime(outcome.InterviewAtUtc),
            pendingCount  = outcome.PendingCount,
            message       = outcome.Message
        });
    }
}
