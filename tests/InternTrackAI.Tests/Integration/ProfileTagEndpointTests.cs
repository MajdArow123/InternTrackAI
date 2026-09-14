using System.Net;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The skills / target-roles save endpoints dedupe case-insensitively on the server and return the
/// normalised list, so a stale client or a direct POST can't store "React" and "react" side by side,
/// and duplicates that are already stored collapse on the next save (first-seen casing wins).
/// </summary>
public class ProfileTagEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ProfileTagEndpointTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<string> UserIdOf(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
    }

    private async Task<UserProfile> ProfileOf(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().UserProfiles.AsNoTracking().SingleAsync(p => p.UserId == userId);
    }

    private async Task SeedRawJson(string userId, string? skillsJson, string? rolesJson)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var p = await db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == userId) ?? db.UserProfiles.Add(new UserProfile { UserId = userId }).Entity;
        p.SkillsJson = skillsJson;
        p.TargetRolesJson = rolesJson;
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> Post(HttpClient client, string url, string field, IEnumerable<string> values)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [field] = JsonSerializer.Serialize(values.ToList()),
                ["__RequestVerificationToken"] = token,
            })
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private static string[] Strings(JsonElement arr) => arr.EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public async Task SaveSkills_dedupes_case_insensitively_and_returns_the_normalised_list()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        var body = await Post(client, "/Profile/SaveSkills", "skillsJson", new[] { "React", "react", " SQL ", "sql", "Type  Script", "", "  " });

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(new[] { "React", "SQL", "Type Script" }, Strings(body.GetProperty("skills")));
        Assert.Equal(new[] { "React", "SQL", "Type Script" }, JsonSerializer.Deserialize<string[]>((await ProfileOf(userId)).SkillsJson!));
    }

    [Fact]
    public async Task SaveTargetRoles_dedupes_the_same_way()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        var body = await Post(client, "/Profile/SaveTargetRoles", "targetRolesJson", new[] { "Backend Developer Intern", "backend developer intern", "Data Science Intern" });

        Assert.Equal(new[] { "Backend Developer Intern", "Data Science Intern" }, Strings(body.GetProperty("targetRoles")));
        Assert.Equal(new[] { "Backend Developer Intern", "Data Science Intern" }, JsonSerializer.Deserialize<string[]>((await ProfileOf(userId)).TargetRolesJson!));
    }

    [Fact]
    public async Task Previously_stored_duplicates_collapse_on_the_next_save_keeping_first_seen_casing()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));
        await SeedRawJson(userId, "[\"React\",\"react\",\"SQL\",\"sql \"]", "[\"Intern\",\"intern\"]");

        // A stale page echoes the stored (duplicated) list back with one more entry.
        var skills = await Post(client, "/Profile/SaveSkills", "skillsJson", new[] { "React", "react", "SQL", "sql ", "Go" });
        Assert.Equal(new[] { "React", "SQL", "Go" }, Strings(skills.GetProperty("skills")));

        var roles = await Post(client, "/Profile/SaveTargetRoles", "targetRolesJson", new[] { "Intern", "intern" });
        Assert.Equal(new[] { "Intern" }, Strings(roles.GetProperty("targetRoles")));

        var profile = await ProfileOf(userId);
        Assert.Equal("[\"React\",\"SQL\",\"Go\"]", profile.SkillsJson);
        Assert.Equal("[\"Intern\"]", profile.TargetRolesJson);
    }

    [Fact]
    public async Task Empty_or_malformed_lists_store_null()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));
        await SeedRawJson(userId, "[\"React\"]", null);

        var body = await Post(client, "/Profile/SaveSkills", "skillsJson", Array.Empty<string>());
        Assert.Empty(body.GetProperty("skills").EnumerateArray());
        Assert.Null((await ProfileOf(userId)).SkillsJson);

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var res = await client.PostAsync("/Profile/SaveSkills", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["skillsJson"] = "not json", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Null((await ProfileOf(userId)).SkillsJson);
    }
}
