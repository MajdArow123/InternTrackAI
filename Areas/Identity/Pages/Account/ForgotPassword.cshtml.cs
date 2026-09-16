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

    public ForgotPasswordModel(
        UserManager<IdentityUser> userManager,
        IAppEmailSender email,
        IConfiguration config,
        IOptions<DataProtectionTokenProviderOptions> tokenOptions)
    {
        _userManager = userManager;
        _email = email;
        _config = config;
        _tokenOptions = tokenOptions;
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
    /// accidentally depend on what we found: an unknown address, the demo account and a send that failed at
    /// Resend all have to be indistinguishable from a delivered email, or the page becomes a way to test
    /// whether an address has an account here.
    /// </summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        ForgotPasswordConfirmation = true;

        // The shared demo account's password is published on the landing page and fixed by the reseeder;
        // a reset for it would only ever be someone else's doing. Checked before the lookup so no token is
        // even generated, and again on the resolved account because sign-in accepts the user-name form too.
        if (ConfiguredAccounts.IsConfigured(Input.Email, _config, ConfiguredAccounts.DemoEmailKey)) return Page();

        var user = await _userManager.FindByEmailAsync(Input.Email);
        if (user is null) return Page();
        if (ConfiguredAccounts.IsConfigured(user.Email, _config, ConfiguredAccounts.DemoEmailKey)) return Page();

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
