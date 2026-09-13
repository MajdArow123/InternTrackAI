using System.Net;
using System.Net.Http.Headers;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The "Resume used" link between an application and a resume version: new applications default to
/// the active resume (form, Capture, CSV import), the Edit form can override it, foreign ids are
/// dropped, and deleting a resume clears the link without touching the application.
/// </summary>
public class ResumeLinkTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ResumeLinkTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<string> UserIdOf(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        return (await users.FindByEmailAsync(email))!.Id;
    }

    /// <summary>Inserts resume rows directly (the upload endpoint needs a real PDF; the link logic doesn't).</summary>
    private async Task<(int Old, int Active)> SeedResumes(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var old = new ResumeVersion { UserId = userId, VersionNumber = 1, OriginalFileName = "resume-v1.pdf", StoredPath = $"resumes/{userId}/a.pdf", FileSize = 100, IsActive = false };
        var act = new ResumeVersion { UserId = userId, VersionNumber = 2, OriginalFileName = "resume-v2.pdf", StoredPath = $"resumes/{userId}/b.pdf", FileSize = 100, IsActive = true, Label = "Backend focus" };
        db.ResumeVersions.AddRange(old, act);
        await db.SaveChangesAsync();
        return (old.Id, act.Id);
    }

    private async Task<JobApplication> AppNamed(string company)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.JobApplications.AsNoTracking().SingleAsync(a => a.CompanyName == company);
    }

    private static Dictionary<string, string> Form(string token, string company, int? resumeId, string status = "Applied", int? id = null) => new()
    {
        ["CompanyName"]     = company,
        ["RoleTitle"]       = "Intern",
        ["Status"]          = status,
        ["WorkMode"]        = "Remote",
        ["ResumeVersionId"] = resumeId?.ToString() ?? "",
        ["Id"]              = id?.ToString() ?? "0",
        ["__RequestVerificationToken"] = token,
    };

    [Fact]
    public async Task Create_form_preselects_the_active_resume_and_saves_the_link()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        var (_, active) = await SeedResumes(await UserIdOf(email));

        var page = await client.GetAsync("/JobApplications/Create");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains($"<option value=\"{active}\" selected=\"selected\">", html);
        Assert.Contains("Backend focus (active)", html);
        Assert.Contains("<option value=\"\">None</option>", html);

        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        var res = await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(Form(token, "Linked Co", active)));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        Assert.Equal(active, (await AppNamed("Linked Co")).ResumeVersionId);

        // The list names the resume under the role and hands it to the drawer.
        var list = await (await client.GetAsync("/JobApplications?view=list")).Content.ReadAsStringAsync();
        Assert.Contains("data-resume=\"Backend focus\"", list);
        Assert.Contains("<div class=\"row-resume text-muted\">Backend focus</div>", list);
    }

    [Fact]
    public async Task Capture_lands_on_Create_with_the_active_resume_preselected()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        var (_, active) = await SeedResumes(await UserIdOf(email));

        // No OpenAI key in tests → the analyzer fails fast and Capture falls back to the plain form.
        var res = await client.GetAsync("/Capture?url=" + Uri.EscapeDataString("https://example.com/jobs/1") + "&title=Intern");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.StartsWith("/JobApplications/Create?", location);

        var html = await (await client.GetAsync(location)).Content.ReadAsStringAsync();
        Assert.Contains($"<option value=\"{active}\" selected=\"selected\">", html);
    }

    [Fact]
    public async Task A_resume_id_that_is_not_the_users_own_is_dropped()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        await SeedResumes(await UserIdOf(email));
        var (_, someoneElses) = await SeedResumes("another-user-" + Guid.NewGuid().ToString("N"));

        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        var res = await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(Form(token, "Foreign Co", someoneElses)));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        Assert.Null((await AppNamed("Foreign Co")).ResumeVersionId);
    }

    [Fact]
    public async Task Edit_can_override_the_resume_and_the_choice_persists()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        var (old, active) = await SeedResumes(await UserIdOf(email));

        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(Form(token, "Override Co", active)));
        var app = await AppNamed("Override Co");

        var editHtml = await (await client.GetAsync($"/JobApplications/Edit/{app.Id}")).Content.ReadAsStringAsync();
        Assert.Contains($"<option value=\"{active}\" selected=\"selected\">", editHtml);

        var editToken = await Http.GetAntiforgeryTokenAsync(client, $"/JobApplications/Edit/{app.Id}");
        var res = await client.PostAsync($"/JobApplications/Edit/{app.Id}", new FormUrlEncodedContent(Form(editToken, "Override Co", old, id: app.Id)));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(old, (await AppNamed("Override Co")).ResumeVersionId);

        // And back to "None".
        res = await client.PostAsync($"/JobApplications/Edit/{app.Id}", new FormUrlEncodedContent(Form(editToken, "Override Co", null, id: app.Id)));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Null((await AppNamed("Override Co")).ResumeVersionId);
    }

    [Fact]
    public async Task Deleting_a_resume_clears_the_link_and_keeps_the_application()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        var (_, active) = await SeedResumes(await UserIdOf(email));

        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(Form(token, "Survivor Co", active)));
        Assert.Equal(active, (await AppNamed("Survivor Co")).ResumeVersionId);

        var profileToken = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var del = await client.PostAsync("/Profile/DeleteResume", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = active.ToString(),
            ["__RequestVerificationToken"] = profileToken,
        }));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);

        var app = await AppNamed("Survivor Co");
        Assert.Null(app.ResumeVersionId);
        Assert.Equal("Intern", app.RoleTitle);
    }

    [Fact]
    public async Task Renaming_a_resume_changes_its_display_name_and_blank_reverts_to_the_file_name()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        var (old, _) = await SeedResumes(await UserIdOf(email));
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");

        var res = await client.PostAsync("/Profile/RenameResume", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = old.ToString(), ["label"] = "  Data science  ", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"label\":\"Data science\"", await res.Content.ReadAsStringAsync());

        res = await client.PostAsync("/Profile/RenameResume", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = old.ToString(), ["label"] = "", ["__RequestVerificationToken"] = token,
        }));
        Assert.Contains("\"label\":\"resume-v1\"", await res.Content.ReadAsStringAsync());

        var profile = await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.Contains("data-resume-label>resume-v1</span>", profile);
        Assert.Contains("data-resume-label>Backend focus</span>", profile);
    }

    [Fact]
    public async Task Csv_import_links_new_rows_to_the_active_resume()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        var (_, active) = await SeedResumes(await UserIdOf(email));

        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications?view=list");
        var csv = "Company,Role,Location,Work Mode,Status,Deadline,Date Applied,Salary,Job Link\nImported Co,Intern,Remote,Remote,Applied,,,,\n";
        var content = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "apps.csv");

        var res = await client.PostAsync("/JobApplications/ImportCsv", content);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(active, (await AppNamed("Imported Co")).ResumeVersionId);
    }
}
