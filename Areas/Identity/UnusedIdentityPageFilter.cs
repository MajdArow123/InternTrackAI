using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace InternTrackAI.Areas.Identity;

/// <summary>
/// Answers 404 for Identity UI pages this app has no flow for but which its library still maps.
///
/// <para>Today that is exactly one page, <c>/Account/ResendEmailConfirmation</c>, and it is here for a
/// concrete reason rather than tidiness. Registration sets <c>RequireConfirmedAccount = false</c> and never
/// sends a confirmation email, so no user can ever need one resent — but the page is mapped, anonymous, and
/// calls <c>IEmailSender</c>. Once that interface was wired to Resend it became an unauthenticated way to
/// make the app send real mail to any registered address, as fast as it can be posted to: a verified
/// 12 posts → 12 sends, with nothing throttling it. Rate limiting a page that should not answer at all is
/// the wrong repair, so it doesn't answer.</para>
///
/// <para>If email confirmation is ever switched on, drop this page from the registration in Program.cs and
/// give it the same treatment <c>ForgotPassword</c> has: a per-client and per-address limit taken in the page
/// with an unchanged response on refusal. <c>ConfirmEmail</c> and <c>ConfirmEmailChange</c> stay mapped —
/// they consume a token rather than issuing one, and the change-email POST that issues it is behind
/// <see cref="DemoAccountGuardFilter"/>.</para>
/// </summary>
public sealed class UnusedIdentityPageFilter : IAsyncPageFilter
{
    /// <summary>Pages Program.cs switches off. Each needs a reason in the class summary before it goes in.</summary>
    public const string ResendEmailConfirmation = "/Account/ResendEmailConfirmation";

    public static readonly IReadOnlySet<string> DisabledPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ResendEmailConfirmation
    };

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        // Setting Result without awaiting next() short-circuits the handler, so nothing runs and nothing sends.
        // 404 rather than 403: the page simply isn't part of this app.
        context.Result = new NotFoundResult();
        return Task.CompletedTask;
    }
}
