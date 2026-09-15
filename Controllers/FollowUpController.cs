using System.Security.Claims;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InternTrackAI.Controllers;

/// <summary>
/// "Draft follow-up" for applications waiting on a reply: generates a short follow-up email and revises it on a
/// one-line instruction, via <see cref="FollowUpService"/>. Both endpoints are JSON fetch calls from
/// <c>wwwroot/js/follow-up.js</c>, antiforgery-checked (token in the header), rate-limited by the shared "ai" policy
/// and owner-scoped (another user's id is a 404). Nothing is stored: the draft only lives in the modal.
/// The demo account gets a pre-written draft and no revisions, so it never spends credits; both actions carry
/// <see cref="NoAiCallForDemoAttribute"/>, so those demo responses don't take a permit either.
/// </summary>
[Authorize]
public class FollowUpController : Controller
{
    public const string NotWaitingError = "Follow-up drafts are for applications you've applied to and are waiting to hear back on.";

    private readonly FollowUpService _followUps;
    private readonly UserClockProvider _clocks;
    private readonly IConfiguration _config;

    public FollowUpController(FollowUpService followUps, UserClockProvider clocks, IConfiguration config)
    {
        _followUps = followUps;
        _clocks    = clocks;
        _config    = config;
    }

    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Generates a fresh draft. JSON: <c>{ success, subject, body, demo }</c> or <c>{ success:false, error }</c>.</summary>
    [HttpPost("JobApplications/{id:int}/followup")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    [NoAiCallForDemo]
    public async Task<IActionResult> Generate(int id, CancellationToken ct)
    {
        var context = await _followUps.BuildContextAsync(id, UserId(), await _clocks.GetAsync(), ct);
        if (context is null)
            return NotFound(new { success = false, error = "Application not found." });
        if (!context.IsWaitingOnReply)
            return Json(new { success = false, error = NotWaitingError });

        if (ConfiguredAccounts.IsDemoUser(User, _config))
        {
            var demo = FollowUpService.DemoDraft(context);
            return Json(new { success = true, subject = demo.Subject, body = demo.Body, demo = true });
        }

        var result = await _followUps.GenerateAsync(context, ct);
        return result.Success
            ? Json(new { success = true, subject = result.Draft!.Subject, body = result.Draft.Body, demo = false })
            : Json(new { success = false, error = result.Error });
    }

    public sealed class ImproveRequest
    {
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? Instruction { get; set; }
    }

    /// <summary>Revises the draft in the modal per <see cref="ImproveRequest.Instruction"/>. Same JSON shape as <see cref="Generate"/>.</summary>
    [HttpPost("JobApplications/{id:int}/followup/improve")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    [NoAiCallForDemo]
    public async Task<IActionResult> Improve(int id, [FromBody] ImproveRequest? req, CancellationToken ct)
    {
        var context = await _followUps.BuildContextAsync(id, UserId(), await _clocks.GetAsync(), ct);
        if (context is null)
            return NotFound(new { success = false, error = "Application not found." });
        if (!context.IsWaitingOnReply)
            return Json(new { success = false, error = NotWaitingError });

        if (ConfiguredAccounts.IsDemoUser(User, _config))
            return Json(new { success = false, demoRestricted = true, error = ConfiguredAccounts.DemoUnavailableMessage });

        var body        = req?.Body?.Trim() ?? "";
        var instruction = req?.Instruction?.Trim() ?? "";
        if (body.Length == 0)
            return Json(new { success = false, error = "The email body is empty. Generate a draft first." });
        if (instruction.Length == 0)
            return Json(new { success = false, error = "Say what to change, e.g. \"make it shorter\"." });
        if (instruction.Length > FollowUpService.MaxInstructionChars)
            return Json(new { success = false, error = $"Keep the instruction under {FollowUpService.MaxInstructionChars} characters." });
        if (body.Length > FollowUpService.MaxBodyChars)
            return Json(new { success = false, error = "The email is too long to revise. Trim it and try again." });

        var result = await _followUps.ImproveAsync(context, new FollowUpDraft(req?.Subject ?? "", body), instruction, ct);
        return result.Success
            ? Json(new { success = true, subject = result.Draft!.Subject, body = result.Draft.Body, demo = false })
            : Json(new { success = false, error = result.Error });
    }
}
