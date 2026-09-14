using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Uploading a resume runs the AI extraction once and merges the result into the profile
/// (add, never remove; a non-empty name is never overwritten). A failed or rate-limited extraction,
/// or the demo account, still saves the file. "Analyze with AI" reuses the same merge.
/// </summary>
public class ResumeAutoFillTests
{
    private sealed class Harness : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public FakeProfileExtractor Extractor { get; } = new();

        public Harness(params (string Key, string Value)[] settings)
        {
            Factory = Parent.WithWebHostBuilder(b =>
            {
                foreach (var (k, v) in settings) b.UseSetting(k, v);
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IProfileExtractor>();
                    services.AddSingleton<IProfileExtractor>(Extractor);
                });
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public async Task<string> UserIdOf(string email)
        {
            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            return (await users.FindByEmailAsync(email))!.Id;
        }

        public async Task<UserProfile?> ProfileOf(string userId)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
        }

        public async Task SeedProfile(string userId, string? fullName, IEnumerable<string>? skills, IEnumerable<string>? roles)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var p = await db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == userId) ?? db.UserProfiles.Add(new UserProfile { UserId = userId }).Entity;
            p.FullName = fullName;
            p.SkillsJson = skills is null ? null : JsonSerializer.Serialize(skills.ToList());
            p.TargetRolesJson = roles is null ? null : JsonSerializer.Serialize(roles.ToList());
            await db.SaveChangesAsync();
        }

        public async Task<List<ResumeVersion>> ResumesOf(string userId)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ResumeVersions.AsNoTracking().Where(r => r.UserId == userId).OrderBy(r => r.VersionNumber).ToListAsync();
        }

        public string ResolveUpload(string storedPath)
        {
            using var scope = Factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<UploadStorage>().Resolve(storedPath);
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] pdf, string fileName = "resume.pdf")
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "resume", fileName);
        return await client.PostAsync("/Profile/UploadResume", form);
    }

    /// <summary>
    /// Follows the upload redirect and returns the profile page HTML (which carries the TempData toast),
    /// entity-decoded so assertions can use the em dash and apostrophes Razor encodes.
    /// </summary>
    private static async Task<string> ProfilePage(HttpClient client)
    {
        var res = await client.GetAsync("/Profile");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    private static List<string> Tags(string? json) => ProfileTags.FromJson(json);

    [Fact]
    public async Task Upload_runs_the_extraction_once_and_fills_name_skills_and_roles()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var res = await Upload(client, TestPdf.SampleResume());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        Assert.Equal(1, h.Extractor.Calls);
        Assert.Contains("Alex Johnson", h.Extractor.Texts[0]);   // the new file's text, not a placeholder

        var profile = await h.ProfileOf(userId);
        Assert.NotNull(profile);
        Assert.Equal("Alex Johnson", profile!.FullName);
        Assert.Equal(new[] { "Python", "React", "SQL" }, Tags(profile.SkillsJson));
        Assert.Equal(new[] { "Backend Developer Intern" }, Tags(profile.TargetRolesJson));

        var html = await ProfilePage(client);
        Assert.Contains("app-toast-success", html);
        Assert.Contains("Resume uploaded — filled in your name and added 3 skills and 1 target role", html);
        Assert.Contains("id=\"autoFillAdded\"", html);
        Assert.Equal(1, h.Extractor.Calls);   // rendering the page doesn't extract again
    }

    [Fact]
    public async Task Merge_keeps_existing_tags_and_dedupes_case_insensitively()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await h.SeedProfile(userId, "Existing Name", new[] { "React", "SQL" }, new[] { "Data Science Intern" });
        h.Extractor.Next = new ProfileExtraction(true, "Alex Johnson",
            new() { "react", " SQL ", "C#", "Type  Script" }, new() { "data science intern", "Backend Developer Intern" }, null);

        await Upload(client, TestPdf.SampleResume());

        var profile = (await h.ProfileOf(userId))!;
        Assert.Equal("Existing Name", profile.FullName);                                              // never overwritten
        Assert.Equal(new[] { "React", "SQL", "C#", "Type Script" }, Tags(profile.SkillsJson));        // first-seen casing kept, whitespace collapsed
        Assert.Equal(new[] { "Data Science Intern", "Backend Developer Intern" }, Tags(profile.TargetRolesJson));

        var html = await ProfilePage(client);
        Assert.Contains("Resume uploaded — added 2 skills and 1 target role", html);
    }

    [Fact]
    public async Task Nothing_new_is_reported_when_the_profile_already_has_everything()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await h.SeedProfile(userId, "Existing Name", new[] { "python", "REACT", "sql" }, new[] { "backend developer intern" });

        await Upload(client, TestPdf.SampleResume());

        var html = await ProfilePage(client);
        Assert.Contains("Resume uploaded — nothing new to add", html);
        Assert.DoesNotContain("id=\"autoFillAdded\"", html);
        var profile = (await h.ProfileOf(userId))!;
        Assert.Equal(new[] { "python", "REACT", "sql" }, Tags(profile.SkillsJson));   // untouched
    }

    [Fact]
    public async Task Failed_extraction_still_saves_the_file_and_says_so()
    {
        using var h = new Harness();
        h.Extractor.Next = ProfileExtraction.Failed("Request to OpenAI failed. Check your network connection.");
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var res = await Upload(client, TestPdf.SampleResume());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var resumes = await h.ResumesOf(userId);
        var only = Assert.Single(resumes);
        Assert.True(only.IsActive);
        Assert.True(File.Exists(h.ResolveUpload(only.StoredPath)));
        Assert.Equal(1, h.Extractor.Calls);

        var profile = await h.ProfileOf(userId);
        Assert.True(profile is null || (profile.FullName is null && profile.SkillsJson is null));

        var html = await ProfilePage(client);
        Assert.Contains("app-toast-info", html);
        Assert.Contains("wasn't auto-filled", html);
        Assert.Contains("Request to OpenAI failed", html);
        Assert.Contains("id=\"analyzeBtn\"", html);   // manual retry is offered
    }

    [Fact]
    public async Task Unreadable_pdf_still_saves_the_file_without_calling_the_extractor()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Upload(client, TestPdf.WithText("short"));   // valid PDF, under the 50-character floor

        Assert.Single(await h.ResumesOf(userId));
        Assert.Equal(0, h.Extractor.Calls);
        var html = await ProfilePage(client);
        Assert.Contains("wasn't auto-filled", html);
        Assert.Contains("No readable text", html);
    }

    [Fact]
    public async Task Demo_account_uploads_but_skips_the_extraction()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        using var h = new Harness(("Demo:Email", demoEmail), ("Demo:Password", "irrelevant-here-1!"));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client, demoEmail));

        var res = await Upload(client, TestPdf.SampleResume());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        Assert.Single(await h.ResumesOf(userId));
        Assert.Equal(0, h.Extractor.Calls);
        var html = await ProfilePage(client);
        Assert.Contains("Resume uploaded. Auto-fill is skipped on the demo account", html);
    }

    [Fact]
    public async Task Rate_limited_upload_still_saves_the_file_and_counts_against_the_ai_bucket()
    {
        using var h = new Harness(("RateLimiting:AI:PermitLimit", "1"), ("RateLimiting:AI:WindowMinutes", "60"));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Upload(client, TestPdf.SampleResume());             // takes the single permit
        Assert.Equal(1, h.Extractor.Calls);

        await Upload(client, TestPdf.SampleResume(), "v2.pdf");   // bucket empty: upload succeeds, no extraction
        Assert.Equal(1, h.Extractor.Calls);
        Assert.Equal(2, (await h.ResumesOf(userId)).Count);

        var html = await ProfilePage(client);
        Assert.Contains("wasn't auto-filled", html);
        Assert.Contains("limit of 1 AI requests per hour", html);

        // The AI endpoints share the same bucket, so they're rejected too.
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var analyzeReq = new HttpRequestMessage(HttpMethod.Post, "/Profile/AnalyzeResume")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token })
        };
        analyzeReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var analyze = await client.SendAsync(analyzeReq);
        Assert.True(analyze.StatusCode == HttpStatusCode.TooManyRequests, $"{analyze.StatusCode} -> {analyze.Headers.Location}");
    }

    [Fact]
    public async Task Set_active_does_not_extract()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Upload(client, TestPdf.SampleResume());
        await Upload(client, TestPdf.SampleResume(), "v2.pdf");
        Assert.Equal(2, h.Extractor.Calls);

        var first = (await h.ResumesOf(userId)).First();
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var res = await client.PostAsync("/Profile/SetActiveResume", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = first.Id.ToString(), ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(2, h.Extractor.Calls);
    }

    [Fact]
    public async Task Manual_analyze_merges_the_same_way_and_returns_what_was_added()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Upload(client, TestPdf.SampleResume());                       // fills Python/React/SQL + the role
        await h.SeedProfile(userId, "Alex Johnson", new[] { "Python", "React", "SQL", "Kubernetes" }, new[] { "Backend Developer Intern" });
        h.Extractor.Next = new ProfileExtraction(true, "Someone Else", new() { "python", "Go" }, new() { "SRE Intern" }, null);

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Profile/AnalyzeResume")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token })
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("Alex Johnson", body.GetProperty("fullName").GetString());                 // non-empty name kept
        Assert.False(body.GetProperty("nameFilled").GetBoolean());
        Assert.Equal(new[] { "Go" }, body.GetProperty("addedSkills").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "SRE Intern" }, body.GetProperty("addedRoles").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "Python", "React", "SQL", "Kubernetes", "Go" }, body.GetProperty("skills").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("added 1 skill and 1 target role", body.GetProperty("summary").GetString());

        var profile = (await h.ProfileOf(userId))!;
        Assert.Equal(new[] { "Python", "React", "SQL", "Kubernetes", "Go" }, Tags(profile.SkillsJson));   // persisted, nothing removed
        Assert.Equal(new[] { "Backend Developer Intern", "SRE Intern" }, Tags(profile.TargetRolesJson));
    }
}
