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
/// The upload half of the resume flow: what gets stored, what gets rejected, and the rule that
/// survives every failure — <b>the file is always saved</b>, whatever the parse does afterwards.
/// <para>
/// The other half, where a parse becomes profile data, is <see cref="ResumeReviewTests"/>. The split
/// matters: uploading no longer writes to the profile at all, and these tests exist partly to keep it
/// that way.
/// </para>
/// </summary>
public class ResumeUploadTests
{
    /// <summary>Shared harness for both resume test files: a real host with a scripted parser.</summary>
    internal sealed class Harness : IDisposable
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

        public async Task<List<ParsedResume>> DraftsOf(string userId)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ParsedResumes.AsNoTracking().Where(p => p.UserId == userId)
                .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).ToListAsync();
        }

        public string ResolveUpload(string storedPath)
        {
            using var scope = Factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<UploadStorage>().Resolve(storedPath);
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    internal static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] bytes, string fileName = "resume.pdf", string contentType = "application/pdf")
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "resume", fileName);
        return await client.PostAsync("/Profile/UploadResume", form);
    }

    /// <summary>The profile page HTML (which carries the TempData toast), entity-decoded for assertions.</summary>
    internal static async Task<string> ProfilePage(HttpClient client)
    {
        var res = await client.GetAsync("/Profile");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    internal static List<string> Tags(string? json) => ProfileTags.FromJson(json);

    // ── What an upload does, and does not do ─────────────────────────────────

    [Fact]
    public async Task Uploading_parses_once_and_writes_nothing_to_the_profile()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var res = await Upload(client, TestPdf.SampleResume());

        // Straight to the review screen, not back to the profile.
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Profile/ReviewResume", res.Headers.Location!.ToString());

        Assert.Equal(1, h.Extractor.Calls);
        Assert.Contains("Alex Johnson", h.Extractor.Texts[0]);   // the new file's text, not a placeholder

        // The whole point of the phase: a parse happened and the profile is untouched.
        var profile = await h.ProfileOf(userId);
        Assert.Null(profile!.SkillsJson);
        Assert.Null(profile.TargetRolesJson);
        Assert.Null(profile.Field);
        Assert.Null(profile.ProfileLastEnrichedAt);

        var draft = Assert.Single(await h.DraftsOf(userId));
        Assert.False(draft.Applied);
        Assert.True(draft.CharactersExtracted > 0);
    }

    [Fact]
    public async Task A_docx_resume_is_accepted_and_its_text_reaches_the_parser()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var res = await Upload(client, TestDocx.SampleResume(), "resume.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var only = Assert.Single(await h.ResumesOf(userId));
        Assert.EndsWith(".docx", only.StoredPath);
        Assert.Equal(1, h.Extractor.Calls);
        Assert.Contains("Alex Johnson", h.Extractor.Texts[0]);
        Assert.Contains("PostgreSQL", h.Extractor.Texts[0]);
    }

    [Fact]
    public async Task A_docx_that_lays_its_skills_out_in_a_table_still_yields_them()
    {
        // The layout a large share of real resumes use; walking only body paragraphs drops all of it.
        using var h = new Harness();
        var client = h.Client();
        await Http.RegisterAsync(client);

        await Upload(client, TestDocx.WithSkillsTable(
            "Jordan Lee - Registered Nurse with acute care placement experience across two hospitals, "
            + "including a medical-surgical rotation and a telemetry floor placement during the final year "
            + "of a BScN at Toronto Metropolitan University.",
            "Patient Assessment", "Medication Administration", "Wound Care"), "resume.docx");

        Assert.Equal(1, h.Extractor.Calls);
        Assert.Contains("Medication Administration", h.Extractor.Texts[0]);
        Assert.Contains("Wound Care", h.Extractor.Texts[0]);
    }

    // ── Rejections: the file is identified by its bytes, never its name ──────

    public static TheoryData<byte[], string> DisguisedFiles() => new()
    {
        // Real MZ bytes, not an ASCII stand-in — see NotResumes in ResumeFileTypeTests.
        { new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 }, "resume.pdf" },   // .exe renamed to .pdf
        { new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 }, "resume.docx" },  // and to .docx
        { System.Text.Encoding.ASCII.GetBytes("Just a text file pretending to be a resume."), "resume.pdf" },
    };

    [Theory]
    [MemberData(nameof(DisguisedFiles))]
    public async Task A_file_that_is_not_a_resume_is_rejected_whatever_it_is_called(byte[] content, string fileName)
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Upload(client, content, fileName);

        Assert.Empty(await h.ResumesOf(userId));
        Assert.Empty(await h.DraftsOf(userId));
        Assert.Equal(0, h.Extractor.Calls);
        Assert.Contains("must be a PDF or Word (.docx) file", await ProfilePage(client));
    }

    [Fact]
    public async Task A_plain_zip_renamed_to_docx_is_rejected()
    {
        // It passes the ZIP magic number, which is exactly why the check goes on to look for
        // word/document.xml inside the archive rather than stopping at the first four bytes.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Upload(client, TestDocx.PlainZip(), "resume.docx");

        Assert.Empty(await h.ResumesOf(userId));
        Assert.Equal(0, h.Extractor.Calls);
        Assert.Contains("must be a PDF or Word (.docx) file", await ProfilePage(client));
    }

    [Fact]
    public async Task A_pdf_renamed_to_docx_is_still_accepted_as_the_pdf_it_is()
    {
        // The name is a hint and the bytes are the truth. Storing it as .pdf is what keeps the text
        // extractor pointed at the right parser later.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Upload(client, TestPdf.SampleResume(), "resume.docx");

        var only = Assert.Single(await h.ResumesOf(userId));
        Assert.EndsWith(".pdf", only.StoredPath);
        Assert.Equal(1, h.Extractor.Calls);
    }

    // ── The file is saved no matter what the parse does ──────────────────────

    [Fact]
    public async Task A_failed_parse_still_saves_the_file_and_says_so()
    {
        using var h = new Harness();
        h.Extractor.Next = ProfileExtraction.Failed("Request to OpenAI failed. Check your network connection.");
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var res = await Upload(client, TestPdf.SampleResume());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var only = Assert.Single(await h.ResumesOf(userId));
        Assert.True(only.IsActive);
        Assert.True(File.Exists(h.ResolveUpload(only.StoredPath)));
        Assert.Equal(1, h.Extractor.Calls);
        Assert.Empty(await h.DraftsOf(userId));

        var html = await ProfilePage(client);
        Assert.Contains("app-toast-info", html);
        Assert.Contains("we couldn't analyze it", html);
        Assert.Contains("Request to OpenAI failed", html);
        Assert.Contains("id=\"analyzeBtn\"", html);   // manual retry is offered
    }

    [Fact]
    public async Task An_unreadable_file_still_saves_without_calling_the_parser()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Upload(client, TestPdf.WithText("short"));   // valid PDF, under the 50-character floor

        Assert.Single(await h.ResumesOf(userId));
        Assert.Equal(0, h.Extractor.Calls);
        var html = await ProfilePage(client);
        Assert.Contains("we couldn't read it", html);
        Assert.Contains("No readable text", html);
    }

    [Fact]
    public async Task A_file_with_almost_no_text_is_called_out_as_a_probable_scan()
    {
        // Above the 50-character "is there anything here" floor but far below a real resume: the
        // signature of a scanned or photographed page. OCR is out of scope, so say so plainly.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Upload(client, TestPdf.WithText("Jordan Lee", "Resume", "Experience and education follow."));

        Assert.Single(await h.ResumesOf(userId));
        Assert.Equal(0, h.Extractor.Calls);   // nothing worth spending a call on
        Assert.Empty(await h.DraftsOf(userId));

        var html = await ProfilePage(client);
        Assert.Contains("may be a scanned image", html);
        Assert.Contains("text-based PDF", html);
    }

    [Fact]
    public async Task A_rate_limited_upload_still_saves_the_file_and_draws_on_the_shared_ai_bucket()
    {
        using var h = new Harness(("RateLimiting:AI:PermitLimit", "1"), ("RateLimiting:AI:WindowMinutes", "60"));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Upload(client, TestPdf.SampleResume());             // takes the single permit
        Assert.Equal(1, h.Extractor.Calls);

        await Upload(client, TestPdf.SampleResume(), "v2.pdf");   // bucket empty: upload succeeds, no parse
        Assert.Equal(1, h.Extractor.Calls);
        Assert.Equal(2, (await h.ResumesOf(userId)).Count);

        var html = await ProfilePage(client);
        Assert.Contains("we couldn't analyze it", html);
        Assert.Contains("limit of 1 AI requests per hour", html);

        // The AI endpoints share the same bucket, so re-parse is refused too. It is a plain form post,
        // so the "ai" policy takes its non-XHR path: a redirect back with an error toast rather than a
        // bare 429, which would read as the page breaking (CLAUDE.md §8).
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var reparse = await client.PostAsync("/Profile/ReparseResume",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, reparse.StatusCode);

        Assert.Equal(1, h.Extractor.Calls);   // refused before the model was called
        Assert.Contains("limit of 1 AI requests per hour", await ProfilePage(client));
    }

    [Fact]
    public async Task Set_active_does_not_parse()
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
    public async Task Downloading_a_docx_serves_it_as_a_word_document()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Upload(client, TestDocx.SampleResume(), "resume.docx");

        var only = Assert.Single(await h.ResumesOf(userId));
        var res = await client.GetAsync($"/Profile/DownloadResume/{only.Id}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            res.Content.Headers.ContentType!.MediaType);
    }
}
