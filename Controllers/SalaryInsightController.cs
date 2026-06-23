using InternTrackAI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    public SalaryInsightController(SalaryInsightService service)
    {
        _service = service;
    }

    /// <summary>
    /// Estimates a typical compensation range for the role/company/location currently entered
    /// in the form, before the application is saved.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Estimate([FromBody] SalaryInsightRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Role) || string.IsNullOrWhiteSpace(req.Company))
            return Json(new { success = false, error = "Enter a company and role first." });

        var (success, range, note, error) = await _service.EstimateAsync(
            req.Role, req.Company, req.Location, req.WorkMode);

        if (!success)
            return Json(new { success = false, error });

        return Json(new { success = true, range, note });
    }
}

/// <summary>Request payload for <see cref="SalaryInsightController.Estimate"/>.</summary>
public record SalaryInsightRequest(string Role, string Company, string? Location, string? WorkMode);
