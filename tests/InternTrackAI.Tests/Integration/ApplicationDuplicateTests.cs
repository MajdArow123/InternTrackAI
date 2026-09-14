using System.Net;
using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The "Possible duplicate" warning on Create and Edit compares company + role trimmed and
/// case-insensitively; "Save anyway" bypasses it; an application never collides with itself.
/// </summary>
public class ApplicationDuplicateTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ApplicationDuplicateTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<List<JobApplication>> AppsOf(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var uid = (await users.FindByEmailAsync(email))!.Id;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.JobApplications.AsNoTracking().Where(a => a.UserId == uid).OrderBy(a => a.Id).ToListAsync();
    }

    private static Dictionary<string, string> Form(string token, string company, string role, int? id = null, bool? force = null)
    {
        var f = new Dictionary<string, string>
        {
            ["CompanyName"] = company,
            ["RoleTitle"]   = role,
            ["Status"]      = "Applied",
            ["WorkMode"]    = "Remote",
            ["__RequestVerificationToken"] = token,
        };
        if (id is not null) f["Id"] = id.Value.ToString();
        if (force is not null) f[id is null ? "forceCreate" : "forceSave"] = force.Value ? "true" : "false";
        return f;
    }

    private static async Task<HttpResponseMessage> Create(HttpClient client, string company, string role, bool? force = null)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        return await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(Form(token, company, role, force: force)));
    }

    private static async Task<HttpResponseMessage> Edit(HttpClient client, int id, string company, string role, bool? force = null)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, $"/JobApplications/Edit/{id}");
        return await client.PostAsync($"/JobApplications/Edit/{id}", new FormUrlEncodedContent(Form(token, company, role, id, force)));
    }

    [Fact]
    public async Task Create_warns_when_company_and_role_match_ignoring_case_and_whitespace()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, (await Create(client, "Stripe", "Backend Intern")).StatusCode);

        var res  = await Create(client, "  stripe ", "BACKEND intern");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);   // re-rendered, not saved
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("id=\"duplicateWarning\"", html);
        Assert.Contains("Possible duplicate", html);
        Assert.Contains("id=\"saveAnywayBtn\"", html);
        Assert.Contains("duplicate-warning.js", html);
        Assert.Single(await AppsOf(email));

        // A different role at the same company is not a duplicate.
        Assert.Equal(HttpStatusCode.Redirect, (await Create(client, "stripe", "Frontend Intern")).StatusCode);
        Assert.Equal(2, (await AppsOf(email)).Count);
    }

    [Fact]
    public async Task Create_save_anyway_bypasses_the_warning_and_stores_trimmed_names()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        await Create(client, "Notion", "Intern");

        var res = await Create(client, "  notion  ", " intern ", force: true);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var apps = await AppsOf(email);
        Assert.Equal(2, apps.Count);
        Assert.Equal("notion", apps[1].CompanyName);
        Assert.Equal("intern", apps[1].RoleTitle);
    }

    [Fact]
    public async Task Edit_warns_when_renamed_onto_another_application_but_not_onto_itself()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        await Create(client, "Stripe", "Backend Intern");
        await Create(client, "Notion", "Backend Intern");
        var apps = await AppsOf(email);
        var stripe = apps[0]; var notion = apps[1];

        // Saving an application under its own name is fine (nothing else matches).
        var self = await Edit(client, stripe.Id, "STRIPE", "backend intern");
        Assert.Equal(HttpStatusCode.Redirect, self.StatusCode);
        Assert.Equal("STRIPE", (await AppsOf(email))[0].CompanyName);

        // Renaming Notion onto Stripe's pair shows the warning and doesn't save.
        var clash = await Edit(client, notion.Id, " stripe ", "Backend Intern");
        Assert.Equal(HttpStatusCode.OK, clash.StatusCode);
        var html = await clash.Content.ReadAsStringAsync();
        Assert.Contains("id=\"duplicateWarning\"", html);
        Assert.Contains("name=\"forceSave\"", html);
        Assert.Equal("Notion", (await AppsOf(email))[1].CompanyName);

        // Save anyway goes through.
        var forced = await Edit(client, notion.Id, " stripe ", "Backend Intern", force: true);
        Assert.Equal(HttpStatusCode.Redirect, forced.StatusCode);
        Assert.Equal("stripe", (await AppsOf(email))[1].CompanyName);
    }
}
