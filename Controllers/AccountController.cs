using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace InternTrackAI.Controllers;

/// <summary>
/// One-click demo sign-in used by the landing page. The demo account's credentials come from
/// configuration (<c>Demo:Email</c> / <c>Demo:Password</c>, e.g. the <c>Demo__Email</c> and
/// <c>Demo__Password</c> environment variables on Railway). When either key is missing the
/// landing page hides its demo buttons and this action simply sends visitors to the login page.
/// </summary>
[AllowAnonymous]
public class AccountController : Controller
{
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly IConfiguration _config;
    private readonly ILogger<AccountController> _logger;

    public AccountController(SignInManager<IdentityUser> signInManager, IConfiguration config, ILogger<AccountController> logger)
    {
        _signInManager = signInManager;
        _config = config;
        _logger = logger;
    }

    /// <summary>True when both demo configuration keys are present and non-empty.</summary>
    public static bool IsDemoConfigured(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["Demo:Email"]) && !string.IsNullOrWhiteSpace(config["Demo:Password"]);

    // POST /Account/DemoLogin
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DemoLogin()
    {
        if (!IsDemoConfigured(_config))
            return RedirectToPage("/Account/Login", new { area = "Identity" });

        var email    = _config["Demo:Email"]!;
        var password = _config["Demo:Password"]!;

        // Start from a clean session so a previously signed-in user lands in the demo account.
        if (User.Identity?.IsAuthenticated == true)
            await _signInManager.SignOutAsync();

        var result = await _signInManager.PasswordSignInAsync(email, password, isPersistent: false, lockoutOnFailure: false);
        if (result.Succeeded)
            return RedirectToAction("Dashboard", "Home");

        _logger.LogWarning("Demo login failed for configured demo account ({Result}).", result);
        TempData["Toast"] = "error|The demo account is unavailable right now. Please sign in or create an account.";
        return RedirectToPage("/Account/Login", new { area = "Identity" });
    }
}
