using InternTrackAI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace InternTrackAI.Areas.Identity;

/// <summary>
/// Keeps visitors from taking over the shared demo account through the Identity "Manage" pages. Registered in
/// Program.cs on the whole <c>Identity/Account/Manage</c> folder, which also covers the pages Identity UI serves
/// from its library without being scaffolded here (Email, EnableAuthenticator, ExternalLogins, …): turning on
/// two-factor or changing the password or email on the demo account would lock every other visitor out of it.
///
/// For the demo account (<see cref="ConfiguredAccounts.IsDemoUser"/>), every POST to a <see cref="GuardedPages"/> page
/// is refused with a redirect to Manage/Index and a "Not available on the demo account." toast. A GET to a
/// scaffolded page that renders its own notice (<see cref="RendersOwnNotice"/>) is shown read-only; a GET to any
/// other guarded page redirects the same way. Manage/Index is read-only for the demo account (its only form is the display
/// name); PersonalData (read-only) stays open. The Profile page's Basic info card writes the display name through
/// <c>ProfileController.SaveInfo</c>, an MVC action this filter does not see; that action carries the same guard.
/// </summary>
public sealed class DemoAccountGuardFilter : IAsyncPageFilter
{
    public const string ManageFolder = "/Account/Manage";
    public const string IndexPage    = "/Account/Manage/Index";

    /// <summary>Every page that changes credentials, sign-in factors, the display name or the account's existence.</summary>
    public static readonly IReadOnlySet<string> GuardedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "/Account/Manage/Index",
        "/Account/Manage/ChangePassword",
        "/Account/Manage/SetPassword",
        "/Account/Manage/Email",
        "/Account/Manage/DeletePersonalData",
        "/Account/Manage/TwoFactorAuthentication",
        "/Account/Manage/EnableAuthenticator",
        "/Account/Manage/ResetAuthenticator",
        "/Account/Manage/Disable2fa",
        "/Account/Manage/GenerateRecoveryCodes",
        "/Account/Manage/ShowRecoveryCodes",
        "/Account/Manage/ExternalLogins",
    };

    /// <summary>Scaffolded pages whose GET shows the demo notice in place, with the form disabled.</summary>
    public static readonly IReadOnlySet<string> RendersOwnNotice = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "/Account/Manage/Index",
        "/Account/Manage/ChangePassword",
        "/Account/Manage/DeletePersonalData",
    };

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        var page = context.ActionDescriptor.ViewEnginePath;
        var config = http.RequestServices.GetRequiredService<IConfiguration>();

        if (!GuardedPages.Contains(page) || !ConfiguredAccounts.IsDemoUser(http.User, config))
        {
            await next();
            return;
        }

        var isRead = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
        if (isRead && RendersOwnNotice.Contains(page))
        {
            await next();
            return;
        }

        http.RequestServices.GetRequiredService<ILogger<DemoAccountGuardFilter>>()
            .LogInformation("Demo account guard refused {Method} {Page}.", http.Request.Method, page);

        var tempData = http.RequestServices.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(http);
        tempData["Toast"] = "info|" + ConfiguredAccounts.DemoUnavailableMessage;
        context.Result = new RedirectToPageResult(IndexPage, new { area = "Identity" });
    }
}
