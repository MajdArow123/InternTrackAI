using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace InternTrackAI.Areas.Identity;

/// <summary>
/// Answers 404 on the four Identity UI library pages that otherwise throw — a 500 and an Error line in the
/// log — when there is no two-factor state for them to act on.
///
/// <para><c>LoginWith2fa</c> and <c>LoginWithRecoveryCode</c> are anonymous: a plain GET with no two-factor
/// sign-in in progress threw "Unable to load two-factor authentication user", so anyone could write error
/// lines to the production log in a loop and bury real errors in the noise. <c>Manage/Disable2fa</c> and
/// <c>Manage/GenerateRecoveryCodes</c> throw the same way for a signed-in user without 2FA.</para>
///
/// <para>Why not <see cref="UnusedIdentityPageFilter"/>: these pages are not unused. Nothing in this app links to
/// two-factor setup, but the library's <c>Manage/EnableAuthenticator</c> works for any signed-in user who opens
/// it, and <c>Login.cshtml.cs</c> hands a 2FA user to <c>LoginWith2fa</c>. Switching the pages off outright
/// would lock out anyone who has turned it on. So the page only answers when its precondition holds — a
/// two-factor sign-in in progress, or 2FA enabled — and is a 404 otherwise, exactly as if it were not there.</para>
/// </summary>
public sealed class TwoFactorStateFilter : IAsyncPageFilter
{
    public const string LoginWith2fa          = "/Account/LoginWith2fa";
    public const string LoginWithRecoveryCode = "/Account/LoginWithRecoveryCode";
    public const string Disable2fa            = "/Account/Manage/Disable2fa";
    public const string GenerateRecoveryCodes = "/Account/Manage/GenerateRecoveryCodes";

    /// <summary>Pages that need a two-factor sign-in in progress (anonymous).</summary>
    public static readonly IReadOnlySet<string> SignInPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LoginWith2fa, LoginWithRecoveryCode };

    /// <summary>Pages that need a signed-in user with 2FA enabled.</summary>
    public static readonly IReadOnlySet<string> ManagePages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Disable2fa, GenerateRecoveryCodes };

    private readonly bool _signIn;
    public TwoFactorStateFilter(bool signIn) => _signIn = signIn;

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        bool ready;
        if (_signIn)
        {
            var signIn = services.GetRequiredService<SignInManager<IdentityUser>>();
            ready = await signIn.GetTwoFactorAuthenticationUserAsync() is not null;
        }
        else
        {
            var users = services.GetRequiredService<UserManager<IdentityUser>>();
            var user = await users.GetUserAsync(context.HttpContext.User);
            ready = user is not null && await users.GetTwoFactorEnabledAsync(user);
        }

        if (!ready) { context.Result = new NotFoundResult(); return; }
        await next();
    }
}
