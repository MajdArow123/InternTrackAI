using System.Net;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>The dashboard "Resume performance" card: hidden with one resume, rendered with two, and the profile inline stat.</summary>
public class ResumePerformanceCardTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ResumePerformanceCardTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<string> UserIdOf(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
    }

    private async Task<int> AddResume(string userId, int version, bool active, string label)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var r = new ResumeVersion { UserId = userId, VersionNumber = version, OriginalFileName = $"r{version}.pdf", StoredPath = $"resumes/{userId}/{version}.pdf", FileSize = 1, IsActive = active, Label = label };
        db.ResumeVersions.Add(r);
        await db.SaveChangesAsync();
        return r.Id;
    }

    private async Task AddApps(string userId, int resumeId, int applied, int interview, int offer)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IEnumerable<JobApplication> Make(ApplicationStatus s, int n) => Enumerable.Range(0, n).Select(i =>
            new JobApplication { UserId = userId, CompanyName = $"{s}{i}-{resumeId}", RoleTitle = "Intern", Status = s, ResumeVersionId = resumeId });
        db.JobApplications.AddRange(Make(ApplicationStatus.Applied, applied).Concat(Make(ApplicationStatus.Interview, interview)).Concat(Make(ApplicationStatus.Offer, offer)));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Card_is_hidden_with_one_resume_and_shown_with_two()
    {
        var client = NewClient();
        var uid = await UserIdOf(await Http.RegisterAsync(client));

        var first = await AddResume(uid, 1, active: false, label: "General");
        await AddApps(uid, first, applied: 4, interview: 1, offer: 0);          // 5 sent, 20%

        var html = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("resumePerformanceCard", html);

        var second = await AddResume(uid, 2, active: true, label: "Backend focus");
        await AddApps(uid, second, applied: 2, interview: 0, offer: 1);         // 3 sent, 33%, low confidence

        html = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();
        Assert.Contains("resumePerformanceCard", html);
        Assert.Contains("General has the best response rate so far (20% across 5 applications).", html);
        Assert.Contains($"data-resume-id=\"{second}\" data-sent=\"3\" data-rate=\"33\"", html);
        Assert.Contains("data-bs-title=\"Fewer than 5 applications\"", html);
        Assert.DoesNotContain(ResumeAnalyticsService.NoResumeLabel, html);

        // Profile list carries the same numbers inline.
        var profile = await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.Contains($"data-resume-stat=\"{first}\">&middot; 5 sent &middot; 20% response rate</span>", profile);
        Assert.Contains($"data-resume-stat=\"{second}\">&middot; 3 sent &middot; 33% response rate</span>", profile);
    }
}
