using System.Net;
using System.Net.Http.Json;
using InternTrackAI.Data;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Demo:Email / Admin:Email as typed into a Railway variable: different casing from the registered address,
/// surrounding spaces, a trailing newline. Every place that resolves or recognises a configured account
/// (demo reset, admin gate, demo login, demo rate limit, Gmail demo guard) must still find the same user.
/// </summary>
public class ConfiguredAccountTests
{
    private static HttpClient Client(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>"demo-ab12@example.test" → "  DEMO-AB12@Example.TEST\n": same address, different case, padded.</summary>
    private static string AsTypedInRailway(string email) => "  " + email.ToUpperInvariant().Replace(".TEST", ".Test") + "\n";

    [Theory]
    [InlineData("demo@interntrackai.com", "demo@interntrackai.com", true)]
    [InlineData("demo@interntrackai.com", "Demo@InternTrackAI.com", true)]
    [InlineData("DEMO@INTERNTRACKAI.COM", "demo@interntrackai.com", true)]
    [InlineData("demo@interntrackai.com", "  demo@interntrackai.com \n", true)]
    [InlineData(" Demo@InternTrackAI.com", "demo@interntrackai.com\t", true)]
    [InlineData("other@interntrackai.com", "demo@interntrackai.com", false)]
    [InlineData("demo@interntrackai.com", "   ", false)]
    [InlineData(null, "demo@interntrackai.com", false)]
    [InlineData("demo@interntrackai.com", null, false)]
    public void Configured_email_matching_ignores_case_and_surrounding_whitespace(string? email, string? configured, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:Email"] = configured, ["Admin:Email"] = configured
        }).Build();

        Assert.Equal(expected, ConfiguredAccounts.Matches(email, configured));
        Assert.Equal(expected, AiRateLimiting.IsDemoEmail(email, config));
        Assert.Equal(expected, ConfiguredAccounts.IsConfigured(email, config, ConfiguredAccounts.AdminEmailKey));
    }

    [Fact]
    public void Blank_configuration_reads_as_not_configured_and_real_values_are_trimmed()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:Email"] = " \n", ["Admin:Email"] = "  admin@example.test\r\n"
        }).Build();

        Assert.Null(ConfiguredAccounts.Read(config, ConfiguredAccounts.DemoEmailKey));
        Assert.Equal("admin@example.test", ConfiguredAccounts.Read(config, ConfiguredAccounts.AdminEmailKey));
        Assert.False(DemoResetService.IsEnabled(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:AutoReset"] = "true", ["Demo:Email"] = "   "
        }).Build()));
    }

    [Fact]
    public async Task Seeder_resolves_the_demo_user_from_a_differently_cased_padded_config_email()
    {
        var registered = $"demo-{Guid.NewGuid():N}@example.test";
        using var parent  = new TestAppFactory();
        using var factory = parent.WithWebHostBuilder(b => b.UseSetting("Demo:Email", AsTypedInRailway(registered)));
        await Http.RegisterAsync(Client(factory), registered);

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().ResetAsync();

        Assert.True(result.UserFound);
        Assert.Equal(15, result.Applications);
        var users  = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var demoId = (await users.FindByEmailAsync(registered))!.Id;
        Assert.Equal(15, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().JobApplications.CountAsync(a => a.UserId == demoId));
    }

    [Fact]
    public async Task Seeder_falls_back_to_the_sign_in_user_name_when_the_stored_normalized_email_does_not_match()
    {
        var registered = $"demo-{Guid.NewGuid():N}@example.test";
        using var parent  = new TestAppFactory();
        using var factory = parent.WithWebHostBuilder(b => b.UseSetting("Demo:Email", registered.ToUpperInvariant()));
        await Http.RegisterAsync(Client(factory), registered);

        using (var scope = factory.Services.CreateScope())
        {
            // A row whose email columns drifted from its user name (e.g. edited outside Identity): sign-in still works.
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(u => u.UserName == registered);
            user.NormalizedEmail = null;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().ResetAsync();
            Assert.True(result.UserFound);
        }
    }

    [Fact]
    public async Task Admin_reset_works_when_both_config_emails_differ_in_case_and_whitespace()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@example.test";
        var demoEmail  = $"demo-{Guid.NewGuid():N}@example.test";
        using var parent  = new TestAppFactory();
        using var factory = parent.WithWebHostBuilder(b =>
        {
            b.UseSetting("Admin:Email", AsTypedInRailway(adminEmail));
            b.UseSetting("Demo:Email", AsTypedInRailway(demoEmail));
        });
        await Http.RegisterAsync(Client(factory), demoEmail);

        var admin = Client(factory);
        await Http.RegisterAsync(admin, adminEmail);
        var page = await admin.GetAsync("/Admin/ResetDemo");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains(demoEmail.ToUpperInvariant().Replace(".TEST", ".Test"), await page.Content.ReadAsStringAsync());   // shown trimmed

        var token = await Http.GetAntiforgeryTokenAsync(admin, "/Admin/ResetDemo");
        var post  = await admin.PostAsync("/Admin/ResetDemo", new FormUrlEncodedContent(new Dictionary<string, string> { ["confirm"] = "true", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        var after = await (await admin.GetAsync("/Admin/ResetDemo")).Content.ReadAsStringAsync();
        Assert.Contains("Demo account reset: 15 applications", after);
        Assert.DoesNotContain("No account matches Demo:Email", after);
    }

    [Fact]
    public async Task Admin_reset_says_when_demo_email_is_not_configured()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@example.test";
        using var parent  = new TestAppFactory();
        using var factory = parent.WithWebHostBuilder(b => { b.UseSetting("Admin:Email", adminEmail); b.UseSetting("Demo:Email", "  "); });

        var admin = Client(factory);
        await Http.RegisterAsync(admin, adminEmail);
        var token = await Http.GetAntiforgeryTokenAsync(admin, "/Admin/ResetDemo");
        await admin.PostAsync("/Admin/ResetDemo", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Contains("Demo:Email is not configured", await (await admin.GetAsync("/Admin/ResetDemo")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Demo_login_signs_in_with_a_differently_cased_padded_config_email()
    {
        var registered = $"demo-{Guid.NewGuid():N}@example.test";
        const string password = "Integration-Pass-1!";
        using var parent  = new TestAppFactory();
        using var factory = parent.WithWebHostBuilder(b =>
        {
            b.UseSetting("Demo:Email", AsTypedInRailway(registered));
            b.UseSetting("Demo:Password", password);
        });
        await Http.RegisterAsync(Client(factory), registered, password);

        var visitor = Client(factory);
        var token = await Http.GetAntiforgeryTokenAsync(visitor, "/");
        var res = await visitor.PostAsync("/Account/DemoLogin", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/Home/Dashboard", res.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/Home/Dashboard")).StatusCode);
    }

    [Fact]
    public async Task Demo_rate_limit_applies_with_a_differently_cased_padded_config_email()
    {
        var registered = $"demo-{Guid.NewGuid():N}@example.test";
        using var parent  = new TestAppFactory();
        using var factory = parent.WithWebHostBuilder(b =>
        {
            b.UseSetting("Demo:Email", AsTypedInRailway(registered));
            b.UseSetting("RateLimiting:AI:PermitLimit", "50");
            b.UseSetting("RateLimiting:AI:DemoPermitLimit", "1");
        });
        var demo = Client(factory);
        await Http.RegisterAsync(demo, registered);

        Assert.Equal(HttpStatusCode.BadRequest, (await demo.PostAsJsonAsync("/Analyzer/Analyze", new { jobDescription = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await demo.PostAsJsonAsync("/Analyzer/Analyze", new { jobDescription = "" })).StatusCode);
    }

    [Fact]
    public async Task Gmail_connect_is_refused_for_the_demo_account_with_a_differently_cased_padded_config_email()
    {
        var registered = $"demo-{Guid.NewGuid():N}@example.test";
        var (parent, factory, oauth, _) = GmailTestHost.Boot(true, ("Demo:Email", AsTypedInRailway(registered)));
        using var __ = parent; using var ___ = factory;

        var demo = GmailTestHost.Client(factory);
        await Http.RegisterAsync(demo, registered);

        var res = await demo.GetAsync("/Integrations/Gmail/Connect");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/Profile", res.Headers.Location!.ToString());
        Assert.Null(oauth.LastState);
    }
}
