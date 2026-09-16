using System.Net;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>The anonymous token feed and the owner-scoped single-application download, end to end.</summary>
public class CalendarTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public CalendarTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string Token, string UserId)> SignedInUserAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email  = await Http.RegisterAsync(client);
        var token  = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        return (client, token, (await users.FindByEmailAsync(email))!.Id);
    }

    private async Task<int> SeedAsync(string uid, string company, DateTime? deadline = null, DateTime? interview = null, DateTime? followUp = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var app = new JobApplication { UserId = uid, CompanyName = company, RoleTitle = "Intern, Platform", Status = ApplicationStatus.Applied,
                                       Deadline = deadline, InterviewAt = interview, FollowUpAt = followUp, JobLink = "https://example.com/j/1" };
        db.JobApplications.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    private async Task<string> CalendarTokenAsync(string uid)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await db.UserProfiles.AsNoTracking().SingleAsync(p => p.UserId == uid)).CalendarToken!;
    }

    private static int Events(string ics) => ics.Split("BEGIN:VEVENT").Length - 1;

    [Fact]
    public async Task Profile_page_issues_a_token_and_the_feed_returns_every_event()
    {
        var (client, _, uid) = await SignedInUserAsync();
        var d = TestClock.Today;
        await SeedAsync(uid, "Feed Co, Inc; Ltd", deadline: d.AddDays(5), interview: TestClock.Instant(d.AddDays(2), 14), followUp: TestClock.Instant(d.AddDays(1)));
        await SeedAsync(uid, "Second Co", deadline: d.AddDays(9));
        await SeedAsync(uid, "No Dates Co");

        var profile = await client.GetAsync("/Profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var token = await CalendarTokenAsync(uid);
        Assert.Equal(43, token.Length);
        Assert.Contains($"/Calendar/feed.ics?token={token}", await profile.Content.ReadAsStringAsync());

        var res = await client.GetAsync($"/Calendar/feed.ics?token={token}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/calendar", res.Content.Headers.ContentType!.MediaType);
        var ics = await res.Content.ReadAsStringAsync();
        Assert.Equal(4, Events(ics));
        Assert.Contains("SUMMARY:Interview: Feed Co\\, Inc\\; Ltd — Intern\\, Platform", ics);
        Assert.Contains("URL:https://example.com/j/1", ics);
        Assert.DoesNotContain("No Dates Co", ics);
    }

    [Fact]
    public async Task Feed_is_anonymous_but_rejects_wrong_or_missing_tokens()
    {
        var (client, _, uid) = await SignedInUserAsync();
        await SeedAsync(uid, "Secret Co", deadline: TestClock.Today.AddDays(3));
        await client.GetAsync("/Profile");
        var token = await CalendarTokenAsync(uid);

        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK,       (await anon.GetAsync($"/Calendar/feed.ics?token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/Calendar/feed.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/Calendar/feed.ics?token=")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/Calendar/feed.ics?token={token[..^1]}x")).StatusCode);
        var wrong = await anon.GetAsync("/Calendar/feed.ics?token=" + new string('a', 43));
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.DoesNotContain("Secret Co", await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Regenerating_the_token_invalidates_the_old_feed_url()
    {
        var (client, token, uid) = await SignedInUserAsync();
        await client.GetAsync("/Profile");
        var oldToken = await CalendarTokenAsync(uid);

        var res = await client.PostAsync("/Profile/RegenerateCalendarToken", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        var newUrl = json.GetProperty("url").GetString()!;
        var newToken = await CalendarTokenAsync(uid);

        Assert.NotEqual(oldToken, newToken);
        Assert.EndsWith($"/Calendar/feed.ics?token={newToken}", newUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Calendar/feed.ics?token={oldToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,       (await client.GetAsync($"/Calendar/feed.ics?token={newToken}")).StatusCode);
    }

    [Fact]
    public async Task Single_application_file_is_owner_scoped_and_requires_sign_in()
    {
        var (owner, _, ownerId) = await SignedInUserAsync();
        var (other, _, _)       = await SignedInUserAsync();
        var id = await SeedAsync(ownerId, "Mine Co", interview: TestClock.Instant(TestClock.Today.AddDays(1)));

        var mine = await owner.GetAsync($"/Calendar/application/{id}.ics");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal("text/calendar", mine.Content.Headers.ContentType!.MediaType);
        Assert.Contains("attachment", mine.Content.Headers.ContentDisposition!.DispositionType);
        var ics = await mine.Content.ReadAsStringAsync();
        Assert.Equal(1, Events(ics));
        Assert.Contains($"UID:interview-{id}@interntrackai", ics);

        var theirs = await other.GetAsync($"/Calendar/application/{id}.ics");
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.DoesNotContain("Mine Co", await theirs.Content.ReadAsStringAsync());

        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync($"/Calendar/application/{id}.ics")).StatusCode);
    }
}
