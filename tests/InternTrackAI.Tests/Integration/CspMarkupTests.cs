using System.Net;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The Content-Security-Policy is enforced with <c>script-src 'self'</c> and no <c>'unsafe-inline'</c>
/// (<see cref="Services.SecurityHeaders"/>), so an inline <c>&lt;script&gt;</c>, an <c>on*=</c> attribute or
/// a <c>javascript:</c> URL simply does not run — no server error, no failing request, just a feature that
/// quietly stops working; and a script from another origin is blocked the same way. This renders every page
/// type, signed out and signed in, in the <c>Testing</c> environment — not Development, whose branches differ
/// (the Identity UI's validation partial loads from a CDN everywhere except Development, which is how a
/// Development-only crawl missed it) — and fails on any of the four. JSON data islands (<c>type="application/json"</c>) are data, not script, and are allowed; the one
/// <c>javascript:</c> URL is the bookmarklet, whose job is to be dragged to the bookmarks bar, not clicked.
/// </summary>
public class CspMarkupTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public CspMarkupTests(TestAppFactory factory, Xunit.Abstractions.ITestOutputHelper output) { _factory = factory; _out = output; }

    private static readonly Regex InlineScript = new(@"<script\b(?![^>]*\bsrc\s*=)(?![^>]*type\s*=\s*""application/json"")[^>]*>", RegexOptions.IgnoreCase);
    private static readonly Regex InlineHandler = new(@"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase);
    private static readonly Regex ForeignScript = new(@"<script\b[^>]*\bsrc\s*=\s*[""']\s*(?:https?:)?//", RegexOptions.IgnoreCase);
    private static readonly Regex ScriptUrl = new(@"\b(?:href|src|action|formaction)\s*=\s*[""']\s*javascript:", RegexOptions.IgnoreCase);

    private static List<string> Violations(string url, string html)
    {
        var found = new List<string>();
        foreach (Match m in InlineScript.Matches(html)) found.Add($"{url}: inline script {m.Value}");
        foreach (Match m in ForeignScript.Matches(html)) found.Add($"{url}: script from another origin {m.Value}");
        foreach (Match m in InlineHandler.Matches(html)) found.Add($"{url}: inline handler {m.Value[..Math.Min(120, m.Value.Length)]}");
        if (!url.StartsWith("/Profile/Bookmarklet", StringComparison.OrdinalIgnoreCase))
            foreach (Match m in ScriptUrl.Matches(html)) found.Add($"{url}: javascript: URL {m.Value}");
        return found;
    }

    [Fact]
    public void The_markup_guard_catches_all_three_shapes()
    {
        // A guard that has only ever been seen passing has not been shown to work (CLAUDE.md §12).
        Assert.NotEmpty(Violations("/x", "<script>alert(1)</script>"));
        Assert.NotEmpty(Violations("/x", "<button onclick=\"go()\">x</button>"));
        Assert.NotEmpty(Violations("/x", "<a href=\"javascript:go()\">x</a>"));
        Assert.NotEmpty(Violations("/x", "<script src=\"https://cdnjs.cloudflare.com/x.js\"></script>"));
        Assert.Empty(Violations("/x", "<script src=\"/js/site.js\"></script><script type=\"application/json\" id=\"d\">{}</script><div class=\"onboarding\"></div>"));
    }

    [Fact]
    public async Task No_page_renders_anything_the_enforced_policy_would_block()
    {
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var found = new List<string>();
        foreach (var url in new[] { "/", "/Home/Privacy", "/Home/Terms", "/no/such/page",
                                    "/Identity/Account/Login", "/Identity/Account/Register", "/Identity/Account/ForgotPassword" })
        {
            var res = await anon.GetAsync(url);
            found.AddRange(Violations(url, await res.Content.ReadAsStringAsync()));
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = await Http.RegisterAsync(client);
        int appId;
        using (var scope = _factory.Services.CreateScope())
        {
            var uid = (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication { UserId = uid, CompanyName = "Markup Co", RoleTitle = "Intern", JobDescription = "Requirements: SQL." };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            appId = app.Id;
        }

        // A server-rendered toast too: the layout's TempData block used to be an inline script plus onclick.
        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        var created = await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["CompanyName"] = "Toast Co", ["RoleTitle"] = "Intern", ["Status"] = "0", ["forceCreate"] = "true",
        }));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var afterCreate = await (await client.GetAsync(created.Headers.Location)).Content.ReadAsStringAsync();
        Assert.Contains("id=\"app-toast\"", afterCreate);   // or this check would not have covered the toast
        found.AddRange(Violations("/JobApplications (after create, with toast)", afterCreate));

        foreach (var url in new[] { "/Home/Dashboard", "/JobApplications", "/JobApplications/Board", "/JobApplications/Create",
                                    $"/JobApplications/Edit/{appId}", "/Profile", "/Profile/Bookmarklet", "/CoverLetter/Generate",
                                    $"/InterviewPrep/Prep?appId={appId}", "/Practice", "/Identity/Account/Manage",
                                    "/Identity/Account/Manage/ChangePassword", "/Identity/Account/Manage/DeletePersonalData",
                                    "/Identity/Account/Manage/PersonalData",
                                    // The Identity UI library's own pages — never scaffolded here, so their markup is the
                                    // library's, which is where the environment-switched CDN partial came from.
                                    "/Identity/Account/Manage/Email", "/Identity/Account/Manage/TwoFactorAuthentication",
                                    "/Identity/Account/Manage/EnableAuthenticator", "/Identity/Account/Manage/ResetAuthenticator",
                                    "/Identity/Account/Manage/ExternalLogins", "/Identity/Account/Manage/SetPassword",
                                    "/Identity/Account/Manage/Disable2fa", "/Identity/Account/Manage/GenerateRecoveryCodes",
                                    "/Identity/Account/Lockout", "/Identity/Account/AccessDenied",
                                    "/Identity/Account/ForgotPasswordConfirmation", "/Identity/Account/ResetPasswordConfirmation",
                                    "/Identity/Account/Logout", "/Identity/Account/LoginWith2fa", "/Identity/Account/LoginWithRecoveryCode",
                                    // (The production error page only answers through the exception handler, so it
                                    // cannot be fetched here; ProductionPathTests checks its markup on a real exception.)
                                    })
        {
            var res = await client.GetAsync(url);
            // A redirect or a 404 (SetPassword for a user who has one; the two-factor pages without two-factor state,
            // TwoFactorStateFilter) renders nothing to check. Anything else must be a 200 — a 500 fails here, which is
            // how four library pages that threw without two-factor state were found.
            if (res.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.NotFound) continue;
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)res.StatusCode}");
            found.AddRange(Violations(url, await res.Content.ReadAsStringAsync()));
        }

        Assert.True(found.Count == 0, string.Join("\n", found));
    }
}
