using System.Net;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>Accept / Dismiss over HTTP, their side effects, owner scoping, and where pending suggestions show up in the pages.</summary>
public class SuggestionEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public SuggestionEndpointTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string Token, string UserId)> SignedInUserAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email  = await Http.RegisterAsync(client);
        var token  = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        return (client, token, (await users.FindByEmailAsync(email))!.Id);
    }

    private async Task<(int AppId, int SuggestionId)> SeedAsync(string uid, ApplicationStatus current, ApplicationStatus suggested, DateTime? interviewAt = null, string company = "Stripe")
    {
        using var scope = _factory.Services.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var app = new JobApplication { UserId = uid, CompanyName = company, RoleTitle = "Backend Intern", Status = current, DateApplied = TestClock.Today.AddDays(-3) };
        db.JobApplications.Add(app);
        await db.SaveChangesAsync();
        var s = new StatusSuggestion
        {
            ApplicationId = app.Id, UserId = uid, GmailMessageId = Guid.NewGuid().ToString("N"), SuggestedStatus = suggested, Confidence = 0.83,
            Summary = $"{company} says: {suggested}.", InterviewAt = interviewAt, EmailSubject = $"Next steps at {company}", EmailFrom = $"hr@{company.ToLower()}.com",
            EmailDate = DateTime.UtcNow.AddHours(-3)
        };
        db.StatusSuggestions.Add(s);
        await db.SaveChangesAsync();
        return (app.Id, s.Id);
    }

    private static HttpRequestMessage Ajax(string url, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("RequestVerificationToken", token);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return req;
    }

    private async Task<T> LoadAsync<T>(Func<ApplicationDbContext, Task<T>> q)
    {
        using var scope = _factory.Services.CreateScope();
        return await q(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    [Fact]
    public async Task Accept_applies_the_status_and_interview_time_and_leaves_a_note()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var when = new DateTime(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc);
        var (appId, sid) = await SeedAsync(uid, ApplicationStatus.Applied, ApplicationStatus.Interview, when);

        var res  = await client.SendAsync(Ajax($"/Suggestions/{sid}/accept", token));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.True(json.GetProperty("accepted").GetBoolean());
        Assert.Equal("Interview", json.GetProperty("statusName").GetString());
        Assert.Equal(appId, json.GetProperty("applicationId").GetInt32());
        Assert.Equal(0, json.GetProperty("pendingCount").GetInt32());
        Assert.Contains("2:00 PM", json.GetProperty("interviewAt").GetString());   // default zone America/Toronto (UTC-4 in September)

        var app = await LoadAsync(db => db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == appId));
        Assert.Equal(ApplicationStatus.Interview, app.Status);
        Assert.Equal(when, app.InterviewAt);

        var s = await LoadAsync(db => db.StatusSuggestions.AsNoTracking().SingleAsync(x => x.Id == sid));
        Assert.Equal(SuggestionState.Accepted, s.Status);

        var note = await LoadAsync(db => db.ApplicationNotes.AsNoTracking().SingleAsync(n => n.JobApplicationId == appId));
        Assert.Equal("Status updated from email: Next steps at Stripe", note.Text);
        Assert.Equal(uid, note.UserId);
    }

    [Fact]
    public async Task Accept_without_an_interview_time_keeps_the_existing_one()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var (appId, sid) = await SeedAsync(uid, ApplicationStatus.Interview, ApplicationStatus.Offer);
        var existing = new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.JobApplications.SingleAsync(a => a.Id == appId)).InterviewAt = existing;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Ajax($"/Suggestions/{sid}/accept", token))).StatusCode);

        var app = await LoadAsync(db => db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == appId));
        Assert.Equal(ApplicationStatus.Offer, app.Status);
        Assert.Equal(existing, app.InterviewAt);
    }

    [Fact]
    public async Task Dismiss_records_the_decision_and_changes_nothing_else()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var (appId, sid) = await SeedAsync(uid, ApplicationStatus.Applied, ApplicationStatus.Rejected);

        var res  = await client.SendAsync(Ajax($"/Suggestions/{sid}/dismiss", token));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.False(json.GetProperty("accepted").GetBoolean());
        Assert.Equal("Applied", json.GetProperty("statusName").GetString());

        var app = await LoadAsync(db => db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == appId));
        Assert.Equal(ApplicationStatus.Applied, app.Status);
        Assert.Null(app.InterviewAt);
        Assert.Equal(SuggestionState.Dismissed, (await LoadAsync(db => db.StatusSuggestions.AsNoTracking().SingleAsync(x => x.Id == sid))).Status);
        Assert.Equal(0, await LoadAsync(db => db.ApplicationNotes.CountAsync(n => n.JobApplicationId == appId)));
    }

    [Fact]
    public async Task Resolved_suggestions_cannot_be_acted_on_again()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var (_, sid) = await SeedAsync(uid, ApplicationStatus.Applied, ApplicationStatus.Offer);

        Assert.Equal(HttpStatusCode.OK,       (await client.SendAsync(Ajax($"/Suggestions/{sid}/dismiss", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Ajax($"/Suggestions/{sid}/accept", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Ajax($"/Suggestions/{sid}/dismiss", token))).StatusCode);
    }

    [Fact]
    public async Task Other_users_get_404_and_nothing_changes()
    {
        var (_, _, bobId) = await SignedInUserAsync();
        var (appId, sid) = await SeedAsync(bobId, ApplicationStatus.Applied, ApplicationStatus.Offer);

        var (alice, aliceToken, _) = await SignedInUserAsync();
        var accept  = await alice.SendAsync(Ajax($"/Suggestions/{sid}/accept", aliceToken));
        var dismiss = await alice.SendAsync(Ajax($"/Suggestions/{sid}/dismiss", aliceToken));

        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, dismiss.StatusCode);
        var body = JsonDocument.Parse(await accept.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("success").GetBoolean());

        Assert.Equal(ApplicationStatus.Applied, (await LoadAsync(db => db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == appId))).Status);
        Assert.Equal(SuggestionState.Pending,  (await LoadAsync(db => db.StatusSuggestions.AsNoTracking().SingleAsync(x => x.Id == sid))).Status);
        Assert.Equal(0, await LoadAsync(db => db.ApplicationNotes.CountAsync(n => n.JobApplicationId == appId)));

        // Alice's dashboard and nav know nothing about Bob's suggestion.
        var html = await (await alice.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("inbox-suggestions-list", html);
        Assert.DoesNotContain("Stripe says", html);
    }

    [Fact]
    public async Task Pending_suggestions_show_on_the_dashboard_the_nav_the_list_the_board_and_go_away_when_resolved()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var (appId, sid) = await SeedAsync(uid, ApplicationStatus.Applied, ApplicationStatus.Interview, company: "Notion");

        var dash = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"inbox-suggestions\"", dash);
        Assert.Contains("Notion says: Interview.", dash);
        Assert.Contains("Next steps at Notion", dash);
        Assert.Contains("83%", dash);
        Assert.Contains($"data-suggestion-action=\"accept\" data-suggestion-id=\"{sid}\"", dash);
        Assert.Contains("id=\"nav-suggestions-badge\" data-count=\"1\"", dash);
        // The card sits above Attention.
        Assert.True(dash.IndexOf("id=\"inbox-suggestions\"", StringComparison.Ordinal) < dash.IndexOf("id=\"attention-card\"", StringComparison.Ordinal));

        var list = await (await client.GetAsync("/JobApplications?view=list")).Content.ReadAsStringAsync();
        Assert.Contains("data-suggestion-dot", list);
        Assert.Contains("data-suggestions=\"%5B", list);          // URI-escaped JSON array on the row
        Assert.DoesNotContain("hr@notion.com\" data", list);      // sanity: no raw attribute leakage

        var board = await (await client.GetAsync("/JobApplications/Board")).Content.ReadAsStringAsync();
        Assert.Contains("Inbox suggests: Interview", board);
        Assert.Contains("data-suggestion-dot", board);
        Assert.Contains("drawer-suggestions-section", board);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Ajax($"/Suggestions/{sid}/dismiss", token))).StatusCode);

        var after = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"inbox-suggestions\"", after);
        Assert.Contains("id=\"nav-suggestions-badge\" data-count=\"0\" hidden", after);
        var listAfter = await (await client.GetAsync("/JobApplications?view=list")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-suggestion-dot", listAfter);
    }

    [Fact]
    public async Task Deleting_the_application_takes_its_suggestions_with_it()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var (appId, sid) = await SeedAsync(uid, ApplicationStatus.Applied, ApplicationStatus.Offer);

        var res = await client.PostAsync($"/JobApplications/Delete/{appId}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.True(res.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, res.StatusCode.ToString());

        Assert.Equal(0, await LoadAsync(db => db.StatusSuggestions.CountAsync(s => s.Id == sid)));
    }
}
