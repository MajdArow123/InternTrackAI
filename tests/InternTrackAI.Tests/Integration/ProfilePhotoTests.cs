using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using InternTrackAI.Controllers;
using InternTrackAI.Data;
using InternTrackAI.Helpers;
using InternTrackAI.Models;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The avatar control's endpoints: UploadPhoto answers fetch callers with JSON (url on success,
/// 400 + error on validation), RemovePhoto clears the file and returns the initials to fall back
/// to, and a plain form post still redirects with a toast.
/// </summary>
public class ProfilePhotoTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ProfilePhotoTests(TestAppFactory factory) => _factory = factory;

    // 1×1 transparent PNG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<UserProfile?> ProfileOf(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var uid = (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == uid);
    }

    private string PhotoPath(string fileName)
    {
        using var scope = _factory.Services.CreateScope();
        return Path.Combine(scope.ServiceProvider.GetRequiredService<UploadStorage>().PhotosDirectory, fileName);
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] bytes, string fileName, string contentType, bool ajax = true)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "photo", fileName);
        var req = new HttpRequestMessage(HttpMethod.Post, "/Profile/UploadPhoto") { Content = form };
        if (ajax) req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> Remove(HttpClient client, bool ajax = true)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Profile/RemovePhoto")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token })
        };
        if (ajax) req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Upload_returns_the_photo_url_and_stores_the_file_then_remove_clears_it()
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);

        var res = await Upload(client, Png, "me.png", "image/png");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("success").GetBoolean());
        var url = body.GetProperty("url").GetString()!;
        Assert.Matches(@"^/uploads/photos/[^/?]+\.png\?v=1$", url);

        var profile = (await ProfileOf(email))!;
        Assert.EndsWith(".png", profile.PhotoFileName);
        Assert.Equal(1, profile.PhotoVersion);
        Assert.True(File.Exists(PhotoPath(profile.PhotoFileName!)));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);   // served at the URL the page uses

        // The page renders the photo and the Remove link, not the old buttons.
        var html = await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.Contains("aria-label=\"Change profile photo\"", html);
        Assert.Contains("id=\"removePhotoBtn\"", html);
        Assert.DoesNotContain("Choose photo", html);
        Assert.DoesNotContain("Update photo", html);

        // A second upload replaces the file and bumps the version (cache-bust).
        var again = await Upload(client, Png, "me.jpg", "image/jpeg");
        var url2 = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString()!;
        Assert.EndsWith(".jpg?v=2", url2);
        Assert.False(File.Exists(PhotoPath(profile.PhotoFileName!)));

        var removed = await Remove(client);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var rb = JsonDocument.Parse(await removed.Content.ReadAsStringAsync()).RootElement;
        Assert.True(rb.GetProperty("success").GetBoolean());
        Assert.Equal(email.Substring(0, 1).ToUpperInvariant(), rb.GetProperty("initials").GetString());   // no name yet → email initial

        var after = (await ProfileOf(email))!;
        Assert.Null(after.PhotoFileName);
        Assert.Equal(3, after.PhotoVersion);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(PhotoPath("x"))!, $"{Path.GetFileNameWithoutExtension(profile.PhotoFileName!)}.*"));
    }

    [Theory]
    [InlineData("me.gif", "image/gif", "Photos must be JPG, PNG, or WebP.")]
    [InlineData("me.txt", "text/plain", "Photos must be JPG, PNG, or WebP.")]
    public async Task Upload_rejects_unsupported_types_with_a_json_error(string fileName, string contentType, string error)
    {
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);

        var res = await Upload(client, Png, fileName, contentType);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(error, body.GetProperty("error").GetString());
        Assert.Null((await ProfileOf(email))?.PhotoFileName);
    }

    [Fact]
    public async Task Upload_rejects_files_over_2_mb()
    {
        var client = NewClient();
        await Http.RegisterAsync(client);

        var big = new byte[ProfileController.MaxPhotoBytes + 1];
        Png.CopyTo(big, 0);
        var res = await Upload(client, big, "big.png", "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("under 2 MB", await res.Content.ReadAsStringAsync());

        var okSize = new byte[ProfileController.MaxPhotoBytes];
        Png.CopyTo(okSize, 0);
        Assert.Equal(HttpStatusCode.OK, (await Upload(client, okSize, "ok.png", "image/png")).StatusCode);
    }

    [Fact]
    public async Task Plain_form_posts_still_redirect_with_a_toast()
    {
        var client = NewClient();
        await Http.RegisterAsync(client);

        var res = await Upload(client, Png, "me.png", "image/png", ajax: false);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/Profile", res.Headers.Location!.ToString());
        Assert.Contains("Photo updated.", await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync());

        var removed = await Remove(client, ajax: false);
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Contains("Photo removed.", await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Alex Johnson", "x@example.test", "AJ")]
    [InlineData("  alex  ", "x@example.test", "A")]
    [InlineData("Mary Ann Lee", "x@example.test", "MA")]
    [InlineData(null, "zed@example.test", "Z")]
    [InlineData("", null, "?")]
    public void Initials_follow_the_avatar_rules(string? name, string? email, string expected) =>
        Assert.Equal(expected, ProfileDisplay.Initials(name, email));
}
