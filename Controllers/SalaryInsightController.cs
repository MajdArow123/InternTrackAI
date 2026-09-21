using System.Security.Claims;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InternTrackAI.Controllers;

/// <summary>
/// Exposes the AI salary estimate as a small JSON API consumed via fetch() from the Create/Edit
/// Application pages (see Views/JobApplications/Create.cshtml and Edit.cshtml). It does not
/// render any views itself.
/// </summary>
[Authorize]
public class SalaryInsightController : Controller
{
    private readonly SalaryInsightService _service;
    private readonly IUserContextBuilder _userContext;

    public SalaryInsightController(SalaryInsightService service, IUserContextBuilder userContext)
    {
        _service = service;
        _userContext = userContext;
    }

    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>
    /// Estimates a typical compensation range for the role/company/location currently entered
    /// in the form, before the application is saved.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> Estimate([FromBody] SalaryInsightRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Role) || string.IsNullOrWhiteSpace(req.Company))
            return Json(new { success = false, error = "Enter a company and role first." });

        // The seniority in the profile context is what stops a Senior-level user being quoted
        // intern pay; the field is what stops the estimate being priced as a tech job.
        var (success, range, note, error) = await _service.EstimateAsync(
            req.Role, req.Company, req.Location, req.WorkMode,
            await _userContext.BuildAsync(UserId(), HttpContext.RequestAborted));

        if (!success)
            return Json(new { success = false, error });

        return Json(new { success = true, range, note });
    }
}

/// <summary>Request payload for <see cref="SalaryInsightController.Estimate"/>.</summary>
public record SalaryInsightRequest(string Role, string Company, string? Location, string? WorkMode);
