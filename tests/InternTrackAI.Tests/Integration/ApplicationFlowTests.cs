using System.Net;
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

    /// <summary>Registers a user and gives them one application, returning the signed-in client.</summary>
    private async Task<HttpClient> WithApplication(string company, string role)
    {
        var client = NewClient();
        await Http.RegisterAsync(client);

        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        var create = await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CompanyName"] = company,
            ["RoleTitle"]   = role,
            ["Status"]      = "Applied",
            ["WorkMode"]    = "Remote",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        return client;
    }

    [Theory]
    // The bug: typing the company in lower case found nothing.
    [InlineData("shopify")]
    [InlineData("SHOPIFY")]
    [InlineData("Shopify")]
    [InlineData("shopIFY")]
    public async Task Search_matches_a_company_whatever_case_you_type(string typed)
    {
        var client = await WithApplication("Shopify", "Backend Intern");

        var html = await (await client.GetAsync($"/JobApplications?search={Uri.EscapeDataString(typed)}")).Content.ReadAsStringAsync();

        Assert.Contains("Shopify", html);
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("BACKEND")]
    public async Task Search_matches_a_role_whatever_case_you_type(string typed)
    {
        var client = await WithApplication("Shopify", "Backend Intern");

        var html = await (await client.GetAsync($"/JobApplications?search={Uri.EscapeDataString(typed)}")).Content.ReadAsStringAsync();

        Assert.Contains("Backend Intern", html);
    }

    [Fact]
    public async Task Search_still_excludes_what_does_not_match()
    {
        // The case-insensitive fix must not become "matches everything".
        var client = await WithApplication("Shopify", "Backend Intern");

        var html = await (await client.GetAsync("/JobApplications?search=stripe")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(">Shopify<", html);
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
        var regToken = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/Register");
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
        var createToken = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
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
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/Register");
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
