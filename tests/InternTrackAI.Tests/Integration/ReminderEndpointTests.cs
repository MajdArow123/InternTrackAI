using System.Net;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>Mark contacted / Snooze endpoints, the Needs-attention filter, and the profile setting, over HTTP.</summary>
public class ReminderEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ReminderEndpointTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string Token, string UserId)> SignedInUserAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email  = await Http.RegisterAsync(client);
        var token  = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        return (client, token, (await users.FindByEmailAsync(email))!.Id);
    }

    /// <summary>
    /// "Today" in the user's own time zone, which is what <see cref="ReminderService"/> counts days from.
    /// Seeding from <c>DateTime.UtcNow.Date</c> instead makes every "N days ago" assertion off by one for the
    /// hours when UTC has rolled into tomorrow but the user's zone has not (after 20:00 in Toronto).
    /// </summary>
    private static DateTime Today =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZones.Resolve(null)).Date;

    private async Task<int> SeedAsync(JobApplication app)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.JobApplications.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    private async Task<JobApplication> Reload(int id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().JobApplications.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private static HttpRequestMessage Ajax(string url, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("RequestVerificationToken", token);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return req;
    }

    [Fact]
    public async Task MarkContacted_ajax_stamps_LastContactAt_and_clears_the_follow_up_flag()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var id = await SeedAsync(new JobApplication
        {
            UserId = uid, CompanyName = "Follow Co", RoleTitle = "Intern", Status = ApplicationStatus.Applied,
            DateApplied = Today.AddDays(-10), FollowUpAt = Today.AddDays(-1)
        });

        var res  = await client.SendAsync(Ajax($"/JobApplications/{id}/contacted", token));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.False(json.GetProperty("followUpDue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("followUpAt").ValueKind);

        var app = await Reload(id);
        Assert.NotNull(app.LastContactAt);
        Assert.True((DateTime.UtcNow - app.LastContactAt!.Value).TotalMinutes < 5);
        Assert.Null(app.FollowUpAt);
    }

    [Fact]
    public async Task Snooze_sets_FollowUpAt_three_days_out_and_form_post_redirects_to_Edit()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var id = await SeedAsync(new JobApplication
        {
            UserId = uid, CompanyName = "Snooze Co", RoleTitle = "Intern", Status = ApplicationStatus.Applied,
            DateApplied = Today.AddDays(-10)
        });

        var res = await client.PostAsync($"/JobApplications/{id}/snooze", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }));

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.EndsWith($"/JobApplications/Edit/{id}", res.Headers.Location!.ToString());
        // New accounts are in the default zone (Toronto): the snooze lands on local midnight three days out, stored as UTC.
        var clock = UserClock.For(TimeZones.DefaultZoneId);
        Assert.Equal(clock.StartOfLocalDayUtc(clock.Today.AddDays(ReminderService.SnoozeDays)), (await Reload(id)).FollowUpAt);
    }

    [Fact]
    public async Task Needs_attention_filter_shows_only_flagged_applications()
    {
        var (client, _, uid) = await SignedInUserAsync();
        await SeedAsync(new JobApplication { UserId = uid, CompanyName = "Quiet Co", RoleTitle = "Intern", Status = ApplicationStatus.Applied, DateApplied = Today.AddDays(-1) });
        await SeedAsync(new JobApplication { UserId = uid, CompanyName = "Overdue Co", RoleTitle = "Intern", Status = ApplicationStatus.Saved, Deadline = Today.AddDays(-3) });
        await SeedAsync(new JobApplication { UserId = uid, CompanyName = "Interview Co", RoleTitle = "Intern", Status = ApplicationStatus.Interview, InterviewAt = Today.AddDays(2).AddHours(14) });

        var all = await (await client.GetAsync("/JobApplications?view=list")).Content.ReadAsStringAsync();
        Assert.Contains("Quiet Co", all);
        Assert.Contains("Needs attention <span class=\"filter-pill-count\">2</span>", all);

        var flagged = await (await client.GetAsync("/JobApplications?view=list&attention=true")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Quiet Co", flagged);
        Assert.Contains("Overdue Co", flagged);
        Assert.Contains("Interview Co", flagged);

        var board = await (await client.GetAsync("/JobApplications/Board?attention=true")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Quiet Co", board);
        Assert.Contains("board-tag board-tag--accent\">Interview ", board);
    }

    [Fact]
    public async Task Dashboard_lists_attention_items_with_reasons()
    {
        var (client, _, uid) = await SignedInUserAsync();
        await SeedAsync(new JobApplication { UserId = uid, CompanyName = "Nudge Co", RoleTitle = "Intern", Status = ApplicationStatus.Applied, DateApplied = Today.AddDays(-9) });
        await SeedAsync(new JobApplication { UserId = uid, CompanyName = "Soon Co", RoleTitle = "Intern", Status = ApplicationStatus.Saved, Deadline = Today.AddDays(2) });

        var html = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();
        Assert.Contains("Applied 9 days ago, no reply", html);
        Assert.Contains("Deadline in 2 days", html);
        Assert.Contains("data-reminder-action=\"contacted\"", html);
    }

    [Fact]
    public async Task Profile_follow_up_window_is_validated_and_applied()
    {
        var (client, token, uid) = await SignedInUserAsync();
        await SeedAsync(new JobApplication { UserId = uid, CompanyName = "Window Co", RoleTitle = "Intern", Status = ApplicationStatus.Applied, DateApplied = Today.AddDays(-4) });

        Task<HttpResponseMessage> Save(string days) => client.PostAsync("/Profile/SaveReminderSettings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["followUpAfterDays"] = days, ["__RequestVerificationToken"] = token
        }));

        var bad = JsonDocument.Parse(await (await Save("2")).Content.ReadAsStringAsync()).RootElement;
        Assert.False(bad.GetProperty("success").GetBoolean());

        var ok = JsonDocument.Parse(await (await Save("3")).Content.ReadAsStringAsync()).RootElement;
        Assert.True(ok.GetProperty("success").GetBoolean());

        var dash = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();
        Assert.Contains("Applied 4 days ago, no reply", dash);
    }
}
