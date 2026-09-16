using System.ComponentModel.DataAnnotations;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Areas.Identity.Pages.Account;

public class ForgotPasswordModel : PageModel
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly IAppEmailSender _email;
    private readonly IConfiguration _config;
    private readonly IOptions<DataProtectionTokenProviderOptions> _tokenOptions;
    private readonly PasswordResetLimiter _limiter;
    private readonly ILogger<ForgotPasswordModel> _logger;

    public ForgotPasswordModel(
        UserManager<IdentityUser> userManager,
        IAppEmailSender email,
        IConfiguration config,
        IOptions<DataProtectionTokenProviderOptions> tokenOptions,
        PasswordResetLimiter limiter,
        ILogger<ForgotPasswordModel> logger)
    {
        _userManager = userManager;
        _email = email;
        _config = config;
        _tokenOptions = tokenOptions;
        _limiter = limiter;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = default!;

    public bool ForgotPasswordConfirmation { get; set; }

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = default!;
    }

    /// <summary>
    /// Every path below renders the same confirmation, and the flag is set before any of them so it cannot
    /// accidentally depend on what we found: an unknown address, the demo account, a rate-limited client and
    /// a send that failed at Resend all have to be indistinguishable from a delivered email, or the page
    /// becomes a way to test whether an address has an account here. That is why the two rate limits are
    /// taken here rather than through the <c>"ai"</c> HTTP policy, which answers a rejection with a 429:
    /// every rejection below is the same <c>return Page()</c> the happy path ends on.
    /// </summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        ForgotPasswordConfirmation = true;

        // This endpoint is anonymous and spends real email quota, so bound what one client can spend before
        // doing any work at all — probing unknown addresses has to cost the prober as much as real ones.
        if (!_limiter.Allow(HttpContext.Connection.RemoteIpAddress))
        {
            // No address and no IP in the line: this is still a request from a person who may own the account.
            _logger.LogWarning("Password reset refused: per-client rate limit reached.");
            return Page();
        }

        // The shared demo account's password is published on the landing page and fixed by the reseeder;
        // a reset for it would only ever be someone else's doing. Checked before the lookup so no token is
        // even generated, and again on the resolved account because sign-in accepts the user-name form too.
        if (ConfiguredAccounts.IsConfigured(Input.Email, _config, ConfiguredAccounts.DemoEmailKey)) return Page();

        var user = await _userManager.FindByEmailAsync(Input.Email);
        if (user is null) return Page();
        if (ConfiguredAccounts.IsConfigured(user.Email, _config, ConfiguredAccounts.DemoEmailKey)) return Page();

        // Taken only now that a message is genuinely about to go out, so probes for unknown or demo addresses
        // can't burn a real user's allowance.
        if (!_limiter.AllowSendTo(Input.Email))
        {
            _logger.LogWarning("Password reset refused: per-address rate limit reached.");
            return Page();
        }

        var code = await _userManager.GeneratePasswordResetTokenAsync(user);

        // Request.Scheme, not a hard-coded host: behind Railway's proxy the forwarded headers are what make
        // this come out https (see the forwarded-headers block in Program.cs and ForwardedProtoTests).
        var callbackUrl = Url.Page(
            "/Account/ResetPassword",
            pageHandler: null,
            values: new { code },
            protocol: Request.Scheme) ?? string.Empty;

        // The stated expiry is the token's real lifespan rather than a number typed into the copy.
        // No cancellation token: the send should finish even if the browser goes away mid-request.
        await _email.SendAsync(Input.Email, EmailTemplates.PasswordReset(callbackUrl, _tokenOptions.Value.TokenLifespan));

        return Page();
    }
}
