using System.Net;
using InternTrackAI.Areas.Identity;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// One app with a demo account (configured with different casing and padding, as a Railway variable might be)
/// and the Identity Manage pages. The demo account must not be able to change its password or email, turn on
/// two-factor, link logins or delete itself: not through the scaffolded pages, and not by posting straight to
/// the pages Identity UI serves from its library.
/// </summary>
public class DemoAccountGuardFixture : TestAppFactory, IAsyncLifetime
{
    public const string Password = "Integration-Pass-1!";
    public string DemoEmail { get; } = $"demo-{Guid.NewGuid():N}@example.test";
    public HttpClient Demo { get; private set; } = null!;
    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        App  = WithWebHostBuilder(b => b.UseSetting("Demo:Email", "  " + DemoEmail.ToUpperInvariant() + "\n"));
        Demo = NewClient();
        await Http.RegisterAsync(Demo, DemoEmail, Password);
    }

    public HttpClient NewClient() => App.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<T> WithScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = App.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    public Task<IdentityUser> DemoUser() =>
        WithScope(async sp => (await sp.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(DemoEmail))!);

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
}

public class DemoAccountGuardTests : IClassFixture<DemoAccountGuardFixture>
{
    private const string Manage = "/Identity/Account/Manage";
    private readonly DemoAccountGuardFixture _f;
    public DemoAccountGuardTests(DemoAccountGuardFixture f) => _f = f;

    private static async Task<HttpResponseMessage> PostForm(HttpClient client, string url, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await Http.GetAntiforgeryTokenAsync(client, Manage);
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    private static void AssertRefused(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(Manage, res.Headers.Location!.OriginalString);
    }

    /// <summary>The redirect target shows the info toast carrying the demo notice (TempData survives the redirect).</summary>
    private static async Task AssertToastOnIndex(HttpClient client)
    {
        var html = await (await client.GetAsync(Manage)).Content.ReadAsStringAsync();
        Assert.Contains("app-toast app-toast-info", html);
        Assert.Contains("class=\"app-toast-msg\">Not available on the demo account.</span>", html);   // the span also carries a scoped-CSS attribute
    }

    // ── Account settings page ─────────────────────────────

    [Fact]
    public async Task Account_settings_show_the_demo_notice_instead_of_email_password_and_delete_actions()
    {
        var html = await (await _f.Demo.GetAsync(Manage)).Content.ReadAsStringAsync();
        Assert.Contains("data-demo-guard=\"email\">Not available on the demo account.", html);
        Assert.Contains("data-demo-guard=\"password\">Not available on the demo account.", html);
        Assert.Contains("data-demo-guard=\"delete\">Not available on the demo account.", html);
        Assert.DoesNotContain("href=\"/Identity/Account/Manage/ChangePassword\"", html);
        Assert.DoesNotContain("href=\"/Identity/Account/Manage/DeletePersonalData\"", html);
    }

    [Fact]
    public async Task Regular_accounts_see_no_notice_and_keep_every_action()
    {
        var client = _f.NewClient();
        await Http.RegisterAsync(client);
        var html = await (await client.GetAsync(Manage)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-demo-guard", html);
        Assert.Contains("href=\"/Identity/Account/Manage/ChangePassword\"", html);
        Assert.Contains("href=\"/Identity/Account/Manage/DeletePersonalData\"", html);

        foreach (var page in new[] { "ChangePassword", "Email", "DeletePersonalData", "TwoFactorAuthentication", "EnableAuthenticator" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Manage}/{page}")).StatusCode);
    }

    // ── Change password ───────────────────────────────────

    [Fact]
    public async Task Change_password_page_is_read_only_for_the_demo_account()
    {
        var res = await _f.Demo.GetAsync($"{Manage}/ChangePassword");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("data-demo-guard=\"password\">Not available on the demo account.", html);
        Assert.Contains("<fieldset disabled=\"disabled\">", html);
    }

    [Fact]
    public async Task Change_password_post_is_refused_for_the_demo_account_and_the_password_is_unchanged()
    {
        var res = await PostForm(_f.Demo, $"{Manage}/ChangePassword", new()
        {
            ["Input.OldPassword"] = DemoAccountGuardFixture.Password,
            ["Input.NewPassword"] = "Hijacked-Pass-9!",
            ["Input.ConfirmPassword"] = "Hijacked-Pass-9!",
        });
        AssertRefused(res);
        await AssertToastOnIndex(_f.Demo);

        await _f.WithScope(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<IdentityUser>>();
            var user  = (await users.FindByEmailAsync(_f.DemoEmail))!;
            Assert.True(await users.CheckPasswordAsync(user, DemoAccountGuardFixture.Password));
            Assert.False(await users.CheckPasswordAsync(user, "Hijacked-Pass-9!"));
            return 0;
        });
    }

    [Fact]
    public async Task Regular_accounts_can_still_change_their_password()
    {
        var client = _f.NewClient();
        var email  = await Http.RegisterAsync(client);
        var res = await PostForm(client, $"{Manage}/ChangePassword", new()
        {
            ["Input.OldPassword"] = DemoAccountGuardFixture.Password,
            ["Input.NewPassword"] = "Changed-Pass-2!",
            ["Input.ConfirmPassword"] = "Changed-Pass-2!",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/Identity/Account/Manage", res.Headers.Location!.OriginalString);

        await _f.WithScope(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True(await users.CheckPasswordAsync((await users.FindByEmailAsync(email))!, "Changed-Pass-2!"));
            return 0;
        });
    }

    // ── Change email (Identity UI's built-in Manage/Email page) ──

    [Fact]
    public async Task Change_email_page_redirects_the_demo_account_with_the_notice()
    {
        AssertRefused(await _f.Demo.GetAsync($"{Manage}/Email"));
        await AssertToastOnIndex(_f.Demo);
    }

    [Fact]
    public async Task Change_email_posts_are_refused_for_the_demo_account_and_the_email_is_unchanged()
    {
        AssertRefused(await PostForm(_f.Demo, $"{Manage}/Email?handler=ChangeEmail", new() { ["Input.NewEmail"] = "hijacker@example.test" }));
        AssertRefused(await PostForm(_f.Demo, $"{Manage}/Email?handler=SendVerificationEmail", new()));

        var user = await _f.DemoUser();
        Assert.Equal(_f.DemoEmail, user.Email);
        Assert.Equal(_f.DemoEmail, user.UserName);
    }

    // ── Delete personal data ──────────────────────────────

    [Fact]
    public async Task Delete_account_page_is_read_only_for_the_demo_account()
    {
        var res = await _f.Demo.GetAsync($"{Manage}/DeletePersonalData");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("data-demo-guard=\"delete\">Not available on the demo account.", html);
        Assert.Contains("<fieldset disabled=\"disabled\">", html);
    }

    [Fact]
    public async Task Delete_account_post_is_refused_for_the_demo_account_and_nothing_is_deleted()
    {
        var demoId = (await _f.DemoUser()).Id;
        await _f.WithScope(async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            db.JobApplications.Add(new JobApplication { UserId = demoId, CompanyName = "Kept Co", RoleTitle = "Still Here" });
            return await db.SaveChangesAsync();
        });

        var res = await PostForm(_f.Demo, $"{Manage}/DeletePersonalData", new() { ["Input.Password"] = DemoAccountGuardFixture.Password });
        AssertRefused(res);

        await _f.WithScope(async sp =>
        {
            Assert.NotNull(await sp.GetRequiredService<UserManager<IdentityUser>>().FindByIdAsync(demoId));
            Assert.True(await sp.GetRequiredService<ApplicationDbContext>().JobApplications.AnyAsync(a => a.UserId == demoId && a.CompanyName == "Kept Co"));
            return 0;
        });
        Assert.Equal(HttpStatusCode.OK, (await _f.Demo.GetAsync("/Home/Dashboard")).StatusCode);   // still signed in
    }

    // ── Two-factor, external logins, set password (built-in pages) ──

    [Theory]
    [InlineData("SetPassword")]
    [InlineData("TwoFactorAuthentication")]
    [InlineData("EnableAuthenticator")]
    [InlineData("ResetAuthenticator")]
    [InlineData("Disable2fa")]
    [InlineData("GenerateRecoveryCodes")]
    [InlineData("ShowRecoveryCodes")]
    [InlineData("ExternalLogins")]
    public async Task Built_in_credential_pages_redirect_the_demo_account(string page) =>
        AssertRefused(await _f.Demo.GetAsync($"{Manage}/{page}"));

    [Fact]
    public async Task Enabling_or_resetting_an_authenticator_is_refused_for_the_demo_account()
    {
        AssertRefused(await PostForm(_f.Demo, $"{Manage}/EnableAuthenticator", new() { ["Input.Code"] = "123456" }));
        AssertRefused(await PostForm(_f.Demo, $"{Manage}/ResetAuthenticator", new()));
        AssertRefused(await PostForm(_f.Demo, $"{Manage}/SetPassword", new() { ["Input.NewPassword"] = "Hijacked-Pass-9!", ["Input.ConfirmPassword"] = "Hijacked-Pass-9!" }));

        await _f.WithScope(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<IdentityUser>>();
            var user  = (await users.FindByEmailAsync(_f.DemoEmail))!;
            Assert.False(await users.GetTwoFactorEnabledAsync(user));
            Assert.Null(await users.GetAuthenticatorKeyAsync(user));   // ResetAuthenticator never ran
            return 0;
        });
    }

    // ── Coverage of the guard list ────────────────────────

    /// <summary>
    /// Every page in Identity/Account/Manage, scaffolded or served by Identity UI, is either guarded or on this
    /// explicit list of pages that are safe for the demo account. A new page from an Identity UI upgrade fails
    /// here until someone decides which side it belongs on.
    /// </summary>
    [Fact]
    public void Every_manage_page_is_either_guarded_or_explicitly_allowed()
    {
        var allowed = new[] { "/Account/Manage/Index", "/Account/Manage/PersonalData", "/Account/Manage/DownloadPersonalData" };

        var pages = _f.App.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<PageActionDescriptor>()
            .Where(d => d.AreaName == "Identity" && d.ViewEnginePath.StartsWith(DemoAccountGuardFilter.ManageFolder + "/", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.ViewEnginePath)
            .Distinct()
            .ToList();

        Assert.Contains("/Account/Manage/Email", pages);   // Identity UI's library pages are really in the list
        var unclassified = pages.Where(p => !DemoAccountGuardFilter.GuardedPages.Contains(p) && !allowed.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(unclassified.Count == 0, "Unclassified Manage pages: " + string.Join(", ", unclassified));
    }
}
