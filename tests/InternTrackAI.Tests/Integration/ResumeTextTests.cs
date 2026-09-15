using System.Net.Http.Headers;
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
/// A resume's text is extracted once and stored on its <see cref="ResumeVersion"/>. Uploads store it up
/// front; rows that predate the column (or whose extraction failed) are backfilled on the first read and
/// served from the column afterwards. A parse that produces nothing must leave the column null so it is
/// retried, never poisoned with an empty string.
/// </summary>
public class ResumeTextTests
{
    private sealed class Harness : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }

        public Harness(params (string Key, string Value)[] settings)
        {
            Factory = Parent.WithWebHostBuilder(b =>
            {
                foreach (var (k, v) in settings) b.UseSetting(k, v);
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IProfileExtractor>();
                    services.AddSingleton<IProfileExtractor>(new FakeProfileExtractor());
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

        /// <summary>Runs work against a fresh scope, the way a real request would.</summary>
        public async Task<T> Scoped<T>(Func<IServiceProvider, Task<T>> work)
        {
            using var scope = Factory.Services.CreateScope();
            return await work(scope.ServiceProvider);
        }

        public Task<ResumeVersion> ActiveOf(string userId) => Scoped(async sp =>
            await sp.GetRequiredService<ApplicationDbContext>().ResumeVersions.AsNoTracking()
                .FirstAsync(r => r.UserId == userId && r.IsActive));

        /// <summary>Clears the stored text, standing in for a row uploaded before the column existed.</summary>
        public Task ClearTextOf(int versionId) => Scoped<object?>(async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var row = await db.ResumeVersions.FirstAsync(r => r.Id == versionId);
            row.ExtractedText = null;
            await db.SaveChangesAsync();
            return null;
        });

        /// <summary>Writes a file that passes the %PDF magic-byte check but that PdfPig cannot parse.</summary>
        public Task CorruptFileOf(string storedPath) => Scoped<object?>(sp =>
        {
            var path = sp.GetRequiredService<UploadStorage>().Resolve(storedPath);
            File.WriteAllBytes(path, "%PDF-1.4 not actually a pdf"u8.ToArray());
            return Task.FromResult<object?>(null);
        });

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] pdf)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "resume", "resume.pdf");
        return await client.PostAsync("/Profile/UploadResume", form);
    }

    private static async Task<(Harness H, HttpClient Client, string UserId)> UploadedAsync(byte[]? pdf = null)
    {
        var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Upload(client, pdf ?? TestPdf.SampleResume());
        return (h, client, userId);
    }

    [Fact]
    public async Task Upload_stores_the_extracted_text_on_the_version()
    {
        var (h, _, userId) = await UploadedAsync();
        using var _h = h;

        var version = await h.ActiveOf(userId);
        Assert.NotNull(version.ExtractedText);
        Assert.Contains("Alex Johnson", version.ExtractedText);
    }

    [Fact]
    public async Task A_version_with_no_stored_text_is_backfilled_on_first_read()
    {
        var (h, _, userId) = await UploadedAsync();
        using var _h = h;

        var version = await h.ActiveOf(userId);
        await h.ClearTextOf(version.Id);
        Assert.Null((await h.ActiveOf(userId)).ExtractedText);

        var text = await h.Scoped(sp => sp.GetRequiredService<ResumeTextService>().GetActiveAsync(userId));

        Assert.Equal(ResumeTextStatus.Ok, text.Status);
        Assert.Contains("Alex Johnson", text.Text);
        Assert.Contains("Alex Johnson", (await h.ActiveOf(userId)).ExtractedText);   // written back
    }

    [Fact]
    public async Task Stored_text_is_served_without_reopening_the_file()
    {
        var (h, _, userId) = await UploadedAsync();
        using var _h = h;

        // The text was stored at upload, so the PDF is never opened again — the strongest available
        // proof that the second read does not re-parse.
        var version = await h.ActiveOf(userId);
        await h.Scoped<object?>(sp =>
        {
            sp.GetRequiredService<UploadStorage>().Delete(version.StoredPath);
            return Task.FromResult<object?>(null);
        });

        var text = await h.Scoped(sp => sp.GetRequiredService<ResumeTextService>().GetActiveAsync(userId));

        Assert.Equal(ResumeTextStatus.Ok, text.Status);
        Assert.Contains("Alex Johnson", text.Text);
    }

    [Fact]
    public async Task An_unreadable_pdf_reports_unreadable_and_leaves_the_column_null_so_it_retries()
    {
        var (h, _, userId) = await UploadedAsync();
        using var _h = h;

        var version = await h.ActiveOf(userId);
        await h.ClearTextOf(version.Id);
        await h.CorruptFileOf(version.StoredPath);

        var text = await h.Scoped(sp => sp.GetRequiredService<ResumeTextService>().GetActiveAsync(userId));

        Assert.Equal(ResumeTextStatus.Unreadable, text.Status);
        Assert.Null(text.Text);
        Assert.Null((await h.ActiveOf(userId)).ExtractedText);   // nothing poisoned the row
    }

    [Fact]
    public async Task A_pdf_with_too_little_text_is_not_ok_and_stores_what_it_found()
    {
        var (h, _, userId) = await UploadedAsync(TestPdf.WithText("short"));
        using var _h = h;

        var text = await h.Scoped(sp => sp.GetRequiredService<ResumeTextService>().GetActiveAsync(userId));

        Assert.Equal(ResumeTextStatus.NoText, text.Status);
        Assert.False(text.Ok);
        // It parsed fine, there just isn't enough of it — storing that avoids re-parsing to learn the same thing.
        Assert.Equal("short", (await h.ActiveOf(userId)).ExtractedText);
    }

    [Fact]
    public async Task A_missing_file_reports_file_missing_rather_than_throwing()
    {
        var (h, _, userId) = await UploadedAsync();
        using var _h = h;

        var version = await h.ActiveOf(userId);
        await h.ClearTextOf(version.Id);
        await h.Scoped<object?>(sp =>
        {
            sp.GetRequiredService<UploadStorage>().Delete(version.StoredPath);
            return Task.FromResult<object?>(null);
        });

        var text = await h.Scoped(sp => sp.GetRequiredService<ResumeTextService>().GetActiveAsync(userId));

        Assert.Equal(ResumeTextStatus.FileMissing, text.Status);
    }

    [Fact]
    public async Task A_user_with_no_resume_reports_no_resume()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var text = await h.Scoped(sp => sp.GetRequiredService<ResumeTextService>().GetActiveAsync(userId));

        Assert.Equal(ResumeTextStatus.NoResume, text.Status);
    }

    [Fact]
    public async Task Text_is_read_from_the_callers_own_active_resume_only()
    {
        using var h = new Harness();

        var alice = h.Client();
        var aliceId = await h.UserIdOf(await Http.RegisterAsync(alice));
        await Upload(alice, TestPdf.SampleResume());

        var bob = h.Client();
        var bobId = await h.UserIdOf(await Http.RegisterAsync(bob));
        await Upload(bob, TestPdf.WithText("Bob Stone - Data Analyst", "Skills: R, Stata, Excel, Tableau, SPSS"));

        var forAlice = await h.Scoped(sp => sp.GetRequiredService<ResumeTextService>().GetActiveAsync(aliceId));
        var forBob   = await h.Scoped(sp => sp.GetRequiredService<ResumeTextService>().GetActiveAsync(bobId));

        Assert.Contains("Alex Johnson", forAlice.Text);
        Assert.DoesNotContain("Bob Stone", forAlice.Text);
        Assert.Contains("Bob Stone", forBob.Text);
        Assert.DoesNotContain("Alex Johnson", forBob.Text);
    }

    [Fact]
    public async Task Switching_the_active_resume_switches_the_text()
    {
        var (h, client, userId) = await UploadedAsync();
        using var _h = h;

        await Upload(client, TestPdf.WithText("Alex Johnson - Backend", "Skills: Go, Kafka, Terraform, gRPC, Redis"));

        var second = await h.Scoped(async sp => await sp.GetRequiredService<ApplicationDbContext>()
            .ResumeVersions.AsNoTracking().Where(r => r.UserId == userId).OrderBy(r => r.VersionNumber).LastAsync());

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        await client.PostAsync("/Profile/SetActiveResume", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = second.Id.ToString(),
            ["__RequestVerificationToken"] = token
        }));

        var text = await h.Scoped(sp => sp.GetRequiredService<ResumeTextService>().GetActiveAsync(userId));

        Assert.Contains("Kafka", text.Text);
        Assert.DoesNotContain("Python", text.Text);
    }
}
