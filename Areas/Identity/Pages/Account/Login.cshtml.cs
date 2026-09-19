using System.ComponentModel.DataAnnotations;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Areas.Identity.Pages.Account;

public class LoginModel : PageModel
{
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly ILogger<LoginModel> _logger;
    private readonly IConfiguration _config;
    private readonly IdentityOptions _identityOptions;

    public LoginModel(
        SignInManager<IdentityUser> signInManager,
        ILogger<LoginModel> logger,
        IConfiguration config,
        IOptions<IdentityOptions> identityOptions)
    {
        _signInManager = signInManager;
        _logger = logger;
        _config = config;
        _identityOptions = identityOptions.Value;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Remember me")]
        public bool RememberMe { get; set; }
    }

    /// <summary>
    /// Reduces a caller-supplied returnUrl to something safe to redirect to, falling back to the
    /// home page for anything that isn't a local path. <see cref="ControllerBase.LocalRedirect"/>
    /// refuses an off-site target by throwing, and by then the user is already signed in — so a
    /// crafted <c>?ReturnUrl=https://elsewhere.example/</c> link turned a *successful* login into a
    /// 500. Applied on both handlers, so the two-factor hand-off carries a safe value too, and the
    /// POST re-checks whatever the form sends back rather than trusting the hidden field.
    /// </summary>
    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");

    /// <summary>
    /// What a locked-out visitor is told. Phrased from the configured window so the number in the
    /// message can never drift from the number Identity enforces, and it points at the password
    /// reset, which is the way out that does not involve waiting.
    /// </summary>
    public static string LockedOutMessage(TimeSpan window)
    {
        var minutes = Math.Max(1, (int)Math.Round(window.TotalMinutes));
        var unit    = minutes == 1 ? "minute" : "minutes";
        return $"Too many failed sign-in attempts. This account is locked for {minutes} {unit}. " +
               "Wait and try again, or reset your password.";
    }

    public async Task OnGetAsync(string? returnUrl = null)
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
            ModelState.AddModelError(string.Empty, ErrorMessage);

        returnUrl = SafeReturnUrl(returnUrl);
        // asp-for renders from ModelState before the page property, and ModelState still holds the raw
        // query argument — without this the hidden ReturnUrl field would echo the hostile value back.
        ModelState.Remove(nameof(ReturnUrl));
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl = SafeReturnUrl(returnUrl);

        if (!ModelState.IsValid) return Page();

        // The demo account is shared and its credentials are handed out by the landing page, so any
        // visitor could otherwise lock it for everyone else by mistyping the password five times.
        // It is the one account that keeps the old unlimited-attempts behaviour. ConfiguredAccounts
        // is the only sanctioned way to match Demo:Email (trimmed, case-insensitive).
        var isDemoAccount = ConfiguredAccounts.IsConfigured(Input.Email, _config, ConfiguredAccounts.DemoEmailKey);

        var result = await _signInManager.PasswordSignInAsync(
            Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: !isDemoAccount);

        if (result.Succeeded)
        {
            _logger.LogInformation("User logged in.");
            return LocalRedirect(returnUrl);
        }

        if (result.RequiresTwoFactor)
            return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl, Input.RememberMe });

        if (result.IsLockedOut)
        {
            // Identity returns LockedOut whether or not the password was right, so this is the only
            // place a locked-out visitor learns why nothing works. The old code redirected to a
            // "./Lockout" page that was never scaffolded here; saying it inline keeps the person on
            // the form with their email still filled in.
            _logger.LogWarning("Login blocked: account locked out after {Attempts} failed attempts.",
                _identityOptions.Lockout.MaxFailedAccessAttempts);
            ModelState.AddModelError(string.Empty, LockedOutMessage(_identityOptions.Lockout.DefaultLockoutTimeSpan));
            return Page();
        }

        ModelState.AddModelError(string.Empty, "Incorrect email or password.");
        return Page();
    }
}
