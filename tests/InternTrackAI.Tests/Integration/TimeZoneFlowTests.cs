using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// End to end: a 2:00 PM interview typed by a Toronto user is stored at 18:00Z, comes back as 2:00 PM
/// on the Edit form, the list row and the .ics, and turns into 7:00 PM once the profile says London.
/// </summary>
public class TimeZoneFlowTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public TimeZoneFlowTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string Token, string UserId)> SignedInUserAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email  = await Http.RegisterAsync(client);
        var token  = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        return (client, token, (await users.FindByEmailAsync(email))!.Id);
    }

    private async Task<JobApplication> AppByCompany(string uid, string company)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().JobApplications.AsNoTracking()
            .SingleAsync(a => a.UserId == uid && a.CompanyName == company);
    }

    private static Task<HttpResponseMessage> SaveInfo(HttpClient client, string token, string zone) =>
        client.PostAsync("/Profile/SaveInfo", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fullName"] = "Zone Tester", ["timeZoneId"] = zone, ["__RequestVerificationToken"] = token
        }));

    [Fact]
    public async Task Interview_round_trips_as_2pm_for_Toronto_and_shows_7pm_after_switching_to_London()
    {
        var (client, token, uid) = await SignedInUserAsync();

        // Create: the form posts the user's wall-clock time (a date next week so the reminder chip renders too).
        // A wall-clock date the user types into the form, not a today-relative comparison: every
        // assertion below converts it through the same UserClock, so which calendar day it lands on
        // is irrelevant. Deliberately not TestClock.Today — there is nothing here to keep in step with.
        var day = DateTime.UtcNow.Date.AddDays(5);
        var typed = $"{day:yyyy-MM-dd}T14:00";
        var create = await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CompanyName"] = "Zone Co", ["RoleTitle"] = "Intern", ["Status"] = "Interview", ["WorkMode"] = "Remote",
            ["InterviewAt"] = typed, ["FollowUpAt"] = $"{day:yyyy-MM-dd}", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);

        var app = await AppByCompany(uid, "Zone Co");
        var toronto = UserClock.For("America/Toronto");
        Assert.Equal(toronto.ToUtc(day.AddHours(14)), app.InterviewAt);                       // stored in UTC (18:00Z in summer, 19:00Z in winter)
        Assert.Equal(toronto.ToUtc(day), app.FollowUpAt);
        Assert.NotEqual(day.AddHours(14), app.InterviewAt);

        // Edit form shows the local time again.
        var edit = await (await client.GetAsync($"/JobApplications/Edit/{app.Id}")).Content.ReadAsStringAsync();
        Assert.Contains($"value=\"{typed}", edit);
        Assert.Contains($"value=\"{day:yyyy-MM-dd}\"", edit);   // FollowUpAt date input

        // List row (drawer data) and attention chip.
        var list = await (await client.GetAsync("/JobApplications?view=list")).Content.ReadAsStringAsync();
        Assert.Contains($"data-interview-at=\"{day:MMM d, yyyy} at 2:00 PM\"", list);
        Assert.Contains("2:00 PM", list);
        Assert.DoesNotContain("6:00 PM", list);

        // Per-application .ics: TZID + VTIMEZONE + local wall-clock.
        var ics = await (await client.GetAsync($"/Calendar/application/{app.Id}.ics")).Content.ReadAsStringAsync();
        Assert.Contains($"DTSTART;TZID=America/Toronto:{day:yyyyMMdd}T140000", ics);
        Assert.Contains("TZID:America/Toronto", ics);
        Assert.Contains("BEGIN:VTIMEZONE", ics);

        // Switch to London: same stored instant, different wall-clock.
        var save = await SaveInfo(client, token, "Europe/London");
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var json = JsonDocument.Parse(await save.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("Europe/London", json.GetProperty("timeZoneId").GetString());

        var london = UserClock.For("Europe/London");
        var expected = london.ToLocal(app.InterviewAt!.Value);
        Assert.Equal(app.InterviewAt, (await AppByCompany(uid, "Zone Co")).InterviewAt);   // storage untouched

        var edit2 = await (await client.GetAsync($"/JobApplications/Edit/{app.Id}")).Content.ReadAsStringAsync();
        Assert.Contains($"value=\"{expected:yyyy-MM-dd'T'HH:mm}", edit2);
        var list2 = await (await client.GetAsync("/JobApplications?view=list")).Content.ReadAsStringAsync();
        Assert.Contains($"data-interview-at=\"{london.LocalDateTime(app.InterviewAt)}\"", list2);
        Assert.Contains("7:00 PM", list2);

        var ics2 = await (await client.GetAsync($"/Calendar/application/{app.Id}.ics")).Content.ReadAsStringAsync();
        Assert.Contains($"DTSTART;TZID=Europe/London:{expected:yyyyMMdd'T'HHmmss}", ics2);
        Assert.Contains("TZID:Europe/London", ics2);
        Assert.DoesNotContain("America/Toronto", ics2);

        // Profile page pre-selects the saved zone.
        var profile = await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.Matches(new Regex("<option value=\"Europe/London\" selected[^>]*>London \\(UTC[^)]*\\)</option>"), profile);
        Assert.Contains("<optgroup label=\"America\">", profile);
    }

    [Fact]
    public async Task Note_timestamps_come_back_in_the_users_zone()
    {
        var (client, token, uid) = await SignedInUserAsync();
        await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CompanyName"] = "Note Co", ["RoleTitle"] = "Intern", ["Status"] = "Applied", ["WorkMode"] = "Remote", ["__RequestVerificationToken"] = token
        }));
        var app = await AppByCompany(uid, "Note Co");

        var add = await client.PostAsync("/JobApplications/AddNote", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["appId"] = app.Id.ToString(), ["text"] = "Recruiter called", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var added = JsonDocument.Parse(await add.Content.ReadAsStringAsync()).RootElement;

        DateTime createdUtc;
        using (var scope = _factory.Services.CreateScope())
            createdUtc = (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ApplicationNotes.AsNoTracking().SingleAsync(n => n.JobApplicationId == app.Id)).CreatedAt;

        var toronto = UserClock.For("America/Toronto");
        Assert.Equal(toronto.LocalDateTime(createdUtc), added.GetProperty("createdAt").GetString());
        Assert.NotEqual(UserClock.Utc().LocalDateTime(createdUtc), added.GetProperty("createdAt").GetString());

        await SaveInfo(client, token, "Asia/Tokyo");
        var listed = JsonDocument.Parse(await (await client.GetAsync($"/JobApplications/Notes?appId={app.Id}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(UserClock.For("Asia/Tokyo").LocalDateTime(createdUtc), listed[0].GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task Unknown_zone_is_rejected_and_the_stored_zone_is_kept()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var res  = await SaveInfo(client, token, "Mars/Olympus_Mons");
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("timeZoneId", json.GetProperty("field").GetString());

        using var scope = _factory.Services.CreateScope();
        var zone = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().UserProfiles.AsNoTracking()
            .Where(p => p.UserId == uid).Select(p => p.TimeZoneId).SingleOrDefaultAsync();
        Assert.True(zone is null || zone == TimeZones.DefaultZoneId);
    }
}
