using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using InternTrackAI.Services;

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
        ConfiguredAccounts.Read(config, ConfiguredAccounts.DemoEmailKey) is not null && !string.IsNullOrWhiteSpace(config["Demo:Password"]);

    // POST /Account/DemoLogin
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DemoLogin()
    {
        if (!IsDemoConfigured(_config))
            return RedirectToPage("/Account/Login", new { area = "Identity" });

        // The password is used verbatim (a space can be part of a password); only the address is trimmed.
        var email    = ConfiguredAccounts.Read(_config, ConfiguredAccounts.DemoEmailKey)!;
        var password = _config["Demo:Password"]!;

        // Start from a clean session so a previously signed-in user lands in the demo account.
        if (User.Identity?.IsAuthenticated == true)
            await _signInManager.SignOutAsync();

        // Same resolution as the demo reset (DemoSeeder), so the button and /Admin/ResetDemo always agree on the account.
        var (user, _) = await ConfiguredAccounts.FindAsync(_signInManager.UserManager, email);
        var result = user is null
            ? Microsoft.AspNetCore.Identity.SignInResult.Failed
            : await _signInManager.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: false);
        if (result.Succeeded)
            return RedirectToAction("Dashboard", "Home");

        _logger.LogWarning("Demo login failed for configured demo account (userFound={Found}, {Result}).", user is not null, result);
        TempData["Toast"] = "error|The demo account is unavailable right now. Please sign in or create an account.";
        return RedirectToPage("/Account/Login", new { area = "Identity" });
    }
}
