using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

public class BoardEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public BoardEndpointTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string Token, string UserId)> SignedInUserAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = await Http.RegisterAsync(client);
        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        return (client, token, (await users.FindByEmailAsync(email))!.Id);
    }

    private async Task<int[]> SeedAsync(string userId, ApplicationStatus status, params string[] companies)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var apps = companies.Select(c => new JobApplication { UserId = userId, CompanyName = c, RoleTitle = "Intern", Status = status }).ToList();
        db.JobApplications.AddRange(apps);
        await db.SaveChangesAsync();
        return apps.Select(a => a.Id).ToArray();
    }

    private static HttpRequestMessage Json(string url, object body, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("RequestVerificationToken", token);
        return req;
    }

    private async Task<JobApplication> Reload(int id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().JobApplications.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    [Fact]
    public async Task Move_to_a_valid_status_updates_status_and_order()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var id = (await SeedAsync(uid, ApplicationStatus.Saved, "Mover Co"))[0];

        var res = await client.SendAsync(Json($"/JobApplications/{id}/move", new { status = "interview", boardOrder = 3 }, token));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(id, body.GetProperty("id").GetInt32());
        Assert.Equal("Interview", body.GetProperty("status").GetString());
        Assert.Equal(3, body.GetProperty("boardOrder").GetInt32());

        var app = await Reload(id);
        Assert.Equal(ApplicationStatus.Interview, app.Status);
        Assert.Equal(3, app.BoardOrder);
    }

    [Theory]
    [InlineData("Ghosted")]
    [InlineData("")]
    [InlineData("7")]
    public async Task Move_to_an_invalid_status_returns_400_with_a_message(string status)
    {
        var (client, token, uid) = await SignedInUserAsync();
        var id = (await SeedAsync(uid, ApplicationStatus.Saved, "Invalid Co"))[0];

        var res = await client.SendAsync(Json($"/JobApplications/{id}/move", new { status, boardOrder = 0 }, token));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains("not a valid status", body.GetProperty("error").GetString());
        Assert.Equal(ApplicationStatus.Saved, (await Reload(id)).Status);
    }

    [Fact]
    public async Task Move_requires_the_antiforgery_token()
    {
        var (client, _, uid) = await SignedInUserAsync();
        var id = (await SeedAsync(uid, ApplicationStatus.Saved, "NoToken Co"))[0];
        var res = await client.PostAsJsonAsync($"/JobApplications/{id}/move", new { status = "Applied", boardOrder = 0 });
        // The antiforgery filter answers 400; the app's status-code pages middleware then re-executes
        // the request through the NotFound page (which sets 404). Either way it must not be a success.
        Assert.Contains(res.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound });
        Assert.Equal(ApplicationStatus.Saved, (await Reload(id)).Status);
    }

    [Fact]
    public async Task Moving_another_users_application_returns_404_and_changes_nothing()
    {
        var (_, _, bobId) = await SignedInUserAsync();
        var bobApp = (await SeedAsync(bobId, ApplicationStatus.Saved, "Bob Board Co"))[0];

        var (alice, token, _) = await SignedInUserAsync();
        var res = await alice.SendAsync(Json($"/JobApplications/{bobApp}/move", new { status = "Offer", boardOrder = 0 }, token));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var app = await Reload(bobApp);
        Assert.Equal(ApplicationStatus.Saved, app.Status);
        Assert.Equal(bobId, app.UserId);
    }

    [Fact]
    public async Task Reorder_persists_the_given_order_and_ignores_foreign_or_other_status_ids()
    {
        var (client, token, uid) = await SignedInUserAsync();
        var ids = await SeedAsync(uid, ApplicationStatus.Applied, "A", "B", "C");
        var otherStatus = (await SeedAsync(uid, ApplicationStatus.Saved, "S"))[0];
        var (_, _, bobId) = await SignedInUserAsync();
        var bobs = (await SeedAsync(bobId, ApplicationStatus.Applied, "Bob"))[0];

        var reversed = new[] { ids[2], ids[0], ids[1], otherStatus, bobs };
        var res = await client.SendAsync(Json("/JobApplications/reorder", new { status = "Applied", ids = reversed }, token));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, body.GetProperty("updated").GetInt32());

        Assert.Equal(0, (await Reload(ids[2])).BoardOrder);
        Assert.Equal(1, (await Reload(ids[0])).BoardOrder);
        Assert.Equal(2, (await Reload(ids[1])).BoardOrder);
        Assert.Equal(0, (await Reload(otherStatus)).BoardOrder);   // different status: untouched
        Assert.Equal(0, (await Reload(bobs)).BoardOrder);          // other user: untouched
    }

    [Fact]
    public async Task Reorder_with_an_invalid_status_or_missing_ids_returns_400()
    {
        var (client, token, _) = await SignedInUserAsync();
        var bad = await client.SendAsync(Json("/JobApplications/reorder", new { status = "Nope", ids = new[] { 1 } }, token));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var missing = await client.SendAsync(Json("/JobApplications/reorder", new { status = "Applied" }, token));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Board_endpoints_require_sign_in()
    {
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await anon.PostAsJsonAsync("/JobApplications/1/move", new { status = "Applied", boardOrder = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await anon.PostAsJsonAsync("/JobApplications/reorder", new { status = "Applied", ids = new[] { 1 } })).StatusCode);
    }
}
