using System.Security.Claims;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InternTrackAI.Controllers;

/// <summary>
/// Operator-only actions. Access is granted to the single account whose email matches the
/// <c>Admin:Email</c> setting; everyone else (and every request when the setting is absent)
/// gets a 404 so the endpoint's existence is not advertised.
/// </summary>
[Authorize]
public class AdminController : Controller
{
    private readonly IConfiguration _config;
    private readonly DemoSeeder _seeder;
    private readonly ILogger<AdminController> _logger;

    public AdminController(IConfiguration config, DemoSeeder seeder, ILogger<AdminController> logger)
    {
        _config = config;
        _seeder = seeder;
        _logger = logger;
    }

    private bool IsAdmin()
    {
        var adminEmail = ConfiguredAccounts.Read(_config, ConfiguredAccounts.AdminEmailKey);
        if (adminEmail is null)
        {
            _logger.LogInformation("Admin endpoint requested but Admin:Email is not configured.");
            return false;
        }
        var ok = ConfiguredAccounts.Matches(User.FindFirstValue(ClaimTypes.Email), adminEmail)
              || ConfiguredAccounts.Matches(User.Identity?.Name, adminEmail);
        if (!ok) _logger.LogWarning("Admin endpoint denied for user {UserId}.", User.FindFirstValue(ClaimTypes.NameIdentifier));
        return ok;
    }

    /// <summary>Confirmation page with the reset button and the current auto-reset status.</summary>
    [HttpGet]
    public IActionResult ResetDemo()
    {
        if (!IsAdmin()) return NotFound();

        ViewBag.DemoEmail   = ConfiguredAccounts.Read(_config, ConfiguredAccounts.DemoEmailKey);
        ViewBag.AutoReset   = DemoResetService.IsEnabled(_config);
        ViewBag.ResetTime   = DemoResetService.ResetTime(_config).ToString("HH:mm");
        return View();
    }

    /// <summary>Runs the demo reseed now. Works regardless of <c>Demo:AutoReset</c>.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetDemo(bool confirm)
    {
        if (!IsAdmin()) return NotFound();

        var result = await _seeder.ResetAsync(HttpContext.RequestAborted);
        _logger.LogInformation("Manual demo reset triggered by {UserId}: userFound={Found} apps={Apps}.",
            User.FindFirstValue(ClaimTypes.NameIdentifier), result.UserFound, result.Applications);

        TempData["Toast"] = result.UserFound
            ? $"success|Demo account reset: {result.Applications} applications, {result.Notes} notes, {result.CoverLetters} cover letter, {result.Suggestions} inbox suggestions in {result.Elapsed.TotalSeconds:0.0}s."
            : result.Email is null
                ? "error|Demo:Email is not configured — nothing was reset."
                : "error|No account matches Demo:Email — nothing was reset.";

        return RedirectToAction(nameof(ResetDemo));
    }
}
