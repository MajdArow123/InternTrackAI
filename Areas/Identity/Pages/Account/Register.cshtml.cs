using System.ComponentModel.DataAnnotations;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InternTrackAI.Areas.Identity.Pages.Account;

public class RegisterModel : PageModel
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly ILogger<RegisterModel> _logger;
    private readonly ApplicationDbContext _db;
    private readonly RegistrationLimiter _limiter;

    public RegisterModel(
        UserManager<IdentityUser> userManager,
        SignInManager<IdentityUser> signInManager,
        ILogger<RegisterModel> logger,
        ApplicationDbContext db,
        RegistrationLimiter limiter)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _logger = logger;
        _db = db;
        _limiter = limiter;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = string.Empty;

        [StringLength(50, ErrorMessage = "Display name must be under 50 characters.")]
        [Display(Name = "Display name")]
        public string? DisplayName { get; set; }

        [Required(ErrorMessage = "Password is required.")]
        [StringLength(100, ErrorMessage = "Password must be at least {2} characters.", MinimumLength = 6)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please confirm your password.")]
        [DataType(DataType.Password)]
        [Compare("Password", ErrorMessage = "Passwords do not match.")]
        [Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");
    }

    /// <summary>
    /// The per-client limit is taken here, on a form that is otherwise ready to create an account.
    /// Nothing else bounds this endpoint: it is anonymous, sends no confirmation email and signs the
    /// visitor straight in, so without it accounts can be minted in a loop — and since the AI limiter
    /// partitions on user id, each new account is a fresh AI quota. A refusal re-renders the form
    /// with the reason under a 429; because <see cref="PageResult"/> writes a content type, the
    /// status-code pages middleware leaves the response alone instead of re-executing it as a 404.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        if (!ModelState.IsValid) return Page();

        if (!_limiter.Allow(HttpContext.Connection.RemoteIpAddress, out var retryAfter))
        {
            // No address and no IP in the line — the refused client may well be a real person on a
            // shared connection.
            _logger.LogWarning("Registration refused: per-client rate limit reached.");

            if (retryAfter.HasValue)
                Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.Value.TotalSeconds)).ToString();
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ModelState.AddModelError(string.Empty, RateLimitedMessage(retryAfter));
            return Page();
        }

        var user = new IdentityUser { UserName = Input.Email, Email = Input.Email };
        var result = await _userManager.CreateAsync(user, Input.Password);

        if (result.Succeeded)
        {
            _logger.LogInformation("New user account created.");

            var displayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim();
            if (displayName != null)
            {
                _db.UserProfiles.Add(new UserProfile { UserId = user.Id, DisplayName = displayName });
                await _db.SaveChangesAsync();
            }

            await _signInManager.SignInAsync(user, isPersistent: false);
            return LocalRedirect(returnUrl);
        }

        foreach (var error in result.Errors)
            ModelState.AddModelError(string.Empty, error.Description);

        return Page();
    }

    /// <summary>Rejection copy: what happened, and when it is worth trying again.</summary>
    public static string RateLimitedMessage(TimeSpan? retryAfter)
    {
        var msg = "Too many accounts have been created from this connection.";
        if (retryAfter.HasValue)
        {
            var mins = Math.Max(1, (int)Math.Ceiling(retryAfter.Value.TotalMinutes));
            msg += mins == 1 ? " Try again in about a minute." : $" Try again in about {mins} minutes.";
        }
        else
        {
            msg += " Try again later.";
        }
        return msg;
    }
}
