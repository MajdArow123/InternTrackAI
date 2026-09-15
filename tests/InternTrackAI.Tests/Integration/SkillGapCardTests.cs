using System.Net;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>The dashboard "Skills you're missing most" card: threshold, rendered takeaway/JSON island, and per-user scoping.</summary>
public class SkillGapCardTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public SkillGapCardTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<string> UserIdOf(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
    }

    private async Task AddApp(string userId, string company, string missingJson, ApplicationStatus status = ApplicationStatus.Applied)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.JobApplications.Add(new JobApplication { UserId = userId, CompanyName = company, RoleTitle = "Software Engineering Intern", Status = status, MissingSkillsJson = missingJson });
        await db.SaveChangesAsync();
    }

    private async Task<string> Dashboard(HttpClient client)
    {
        var res = await client.GetAsync("/Home/Dashboard");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Card_is_hidden_at_two_analyzed_applications_and_shown_at_three()
    {
        var client = NewClient();
        var uid = await UserIdOf(await Http.RegisterAsync(client));

        await AddApp(uid, "Acme", "[\"Docker\",\"Go\"]");
        await AddApp(uid, "Globex", "[\"docker\"]", ApplicationStatus.Rejected);
        await AddApp(uid, "Initech", "[\"Docker\"]", ApplicationStatus.Saved);   // never sent: not counted
        await AddApp(uid, "Hooli", "not json");                               // malformed: not counted

        var html = await Dashboard(client);
        Assert.DoesNotContain("skillGapCard", html);
        Assert.DoesNotContain("skillGapData", html);

        await AddApp(uid, "Umbrella", "[\"  Docker \"]", ApplicationStatus.Interview);   // trimmed; "Docker" is now the most common casing

        html = await Dashboard(client);
        Assert.Contains("id=\"skillGapCard\"", html);
        Assert.Contains("Skills you're missing most", html);
        Assert.Contains("Docker came up in 3 of your 3 analyzed applications (100%).", html);
        Assert.Contains("Based on 3 applications — the picture gets clearer as you analyze more.", html);
        Assert.Contains("id=\"skillGapData\"", html);
        Assert.DoesNotContain("Initech", html.Substring(html.IndexOf("id=\"skillGapData\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Another_users_applications_never_appear()
    {
        var alice = NewClient();
        var aliceId = await UserIdOf(await Http.RegisterAsync(alice));
        var bob = NewClient();
        var bobId = await UserIdOf(await Http.RegisterAsync(bob));

        for (var i = 0; i < 3; i++) await AddApp(aliceId, $"AliceCo{i}", "[\"Docker\"]");
        for (var i = 0; i < 5; i++) await AddApp(bobId, $"BobCo{i}", "[\"Haskell\",\"Docker\"]");

        using (var scope = _factory.Services.CreateScope())
        {
            var vm = await scope.ServiceProvider.GetRequiredService<SkillGapService>().GetSkillGapsAsync(aliceId);
            Assert.Equal(3, vm.AnalyzedApplications);
            Assert.Equal(3, Assert.Single(vm.All.Skills).Count);
            Assert.All(vm.Applications.Values, a => Assert.StartsWith("AliceCo", a.Company));
        }

        var html = await Dashboard(alice);
        Assert.Contains("Docker came up in 3 of your 3 analyzed applications (100%).", html);
        Assert.DoesNotContain("Haskell", html);
        Assert.DoesNotContain("BobCo", html);
    }
}
