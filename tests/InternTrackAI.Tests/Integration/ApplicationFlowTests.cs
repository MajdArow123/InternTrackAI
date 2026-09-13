using System.Net;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

public class ApplicationFlowTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public ApplicationFlowTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static readonly Regex TokenRx = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        var m = TokenRx.Match(html);
        Assert.True(m.Success, $"No antiforgery token found on {url}");
        return m.Groups[1].Value;
    }

    [Fact]
    public async Task Applications_page_requires_sign_in()
    {
        var client = NewClient();
        var res = await client.GetAsync("/JobApplications");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Identity/Account/Login", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Register_create_application_and_read_it_back()
    {
        var client = NewClient();
        var email  = $"it-{Guid.NewGuid():N}@example.test";
        const string password = "Integration-Pass-1!";

        // ── Register through the real Identity page (sets the auth cookie) ──
        var regToken = await GetAntiforgeryTokenAsync(client, "/Identity/Account/Register");
        var reg = await client.PostAsync("/Identity/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"]            = email,
            ["Input.DisplayName"]      = "Integration Tester",
            ["Input.Password"]         = password,
            ["Input.ConfirmPassword"]  = password,
            ["__RequestVerificationToken"] = regToken,
        }));
        Assert.Equal(HttpStatusCode.Redirect, reg.StatusCode);
        Assert.Equal("/", reg.Headers.Location!.ToString());

        // ── Create an application ──
        var createToken = await GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        var create = await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CompanyName"] = "Integration Co",
            ["RoleTitle"]   = "Backend Intern",
            ["Location"]    = "Remote",
            ["Status"]      = "Applied",
            ["WorkMode"]    = "Remote",
            ["UserId"]      = "spoofed-user-id",   // must be ignored and replaced server-side
            ["__RequestVerificationToken"] = createToken,
        }));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        Assert.Equal("/JobApplications", create.Headers.Location!.ToString());

        // ── Read it back through the list page ──
        var list = await client.GetAsync("/JobApplications");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var html = await list.Content.ReadAsStringAsync();
        Assert.Contains("Integration Co", html);
        Assert.Contains("Backend Intern", html);
        Assert.Contains("data-status=\"1\"", html);   // Applied

        // ── And directly in the database, owned by the registered user ──
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var db    = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user  = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        var app = await db.JobApplications.SingleAsync(a => a.CompanyName == "Integration Co");
        Assert.Equal(user!.Id, app.UserId);
    }

    [Fact]
    public async Task Another_user_cannot_see_someone_elses_applications()
    {
        // Seed a row for user A directly, then list as a freshly registered user B.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.JobApplications.Add(new Models.JobApplication { UserId = "user-a", CompanyName = "Secret Corp", RoleTitle = "Hidden Role" });
            await db.SaveChangesAsync();
        }

        var client = NewClient();
        var token = await GetAntiforgeryTokenAsync(client, "/Identity/Account/Register");
        var email = $"b-{Guid.NewGuid():N}@example.test";
        await client.PostAsync("/Identity/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email, ["Input.Password"] = "Integration-Pass-1!", ["Input.ConfirmPassword"] = "Integration-Pass-1!",
            ["__RequestVerificationToken"] = token,
        }));

        var html = await (await client.GetAsync("/JobApplications")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Secret Corp", html);
    }
}
