using System.Net;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

public class DemoResetTests
{
    private static HttpClient Client(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Seeder_replaces_the_demo_users_data_and_is_idempotent()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        using var parent  = new TestAppFactory();
        using var factory = parent.WithWebHostBuilder(b => { b.UseSetting("Demo:Email", demoEmail); b.UseSetting("Demo:Password", "x-Demo-1!"); });

        var demo = Client(factory);
        await Http.RegisterAsync(demo, demoEmail);
        var other = Client(factory);
        var otherEmail = await Http.RegisterAsync(other);

        string demoId, otherId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
            demoId  = (await users.FindByEmailAsync(demoEmail))!.Id;
            otherId = (await users.FindByEmailAsync(otherEmail))!.Id;

            var visitorApp = new JobApplication { UserId = demoId, CompanyName = "Visitor Co", RoleTitle = "Left Behind", Status = ApplicationStatus.Saved };
            var otherApp   = new JobApplication { UserId = otherId, CompanyName = "Other Co", RoleTitle = "Untouched" };
            db.JobApplications.AddRange(visitorApp, otherApp);
            await db.SaveChangesAsync();
            db.ApplicationNotes.Add(new ApplicationNote { UserId = demoId, JobApplicationId = visitorApp.Id, Text = "stale note" });
            // Registration already created the profile row; dirty every field the reset is supposed to restore.
            var profile = await db.UserProfiles.SingleOrDefaultAsync(p => p.UserId == demoId) ?? db.UserProfiles.Add(new UserProfile { UserId = demoId }).Entity;
            profile.FullName = "Demo Person";
            profile.Country  = "Nowhere";
            profile.DisplayName = "Renamed By A Visitor";   // slipped past the guard somehow: the reset must put it back
            profile.SkillsJson = "[\"C#\"]";
            profile.TargetRolesJson = "[\"Frontend Engineering Intern\",\"Visitor Role\"]";   // an earlier seed's role + a visitor's
            db.ResumeVersions.Add(new ResumeVersion { UserId = demoId, VersionNumber = 1, OriginalFileName = "r.pdf", StoredPath = "resumes/" + demoId + "/r.pdf", IsActive = true });
            db.ResumeVersions.Add(new ResumeVersion { UserId = demoId, VersionNumber = 2, OriginalFileName = "visitor.pdf", StoredPath = "resumes/" + demoId + "/v.pdf", IsActive = false });
            await db.SaveChangesAsync();
        }

        for (var run = 1; run <= 2; run++)
        {
            using var scope = factory.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().ResetAsync();
            Assert.True(result.UserFound);
            Assert.Equal(15, result.Applications);
            Assert.True(result.Notes >= 2);
            Assert.Equal(1, result.CoverLetters);
            Assert.Equal(3, result.Suggestions);

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var demoApps = await db.JobApplications.Where(a => a.UserId == demoId).ToListAsync();
            Assert.Equal(15, demoApps.Count);                                            // no accumulation on the second run
            Assert.DoesNotContain(demoApps, a => a.CompanyName == "Visitor Co");
            Assert.All(Enum.GetValues<ApplicationStatus>(), st => Assert.Contains(demoApps, a => a.Status == st));
            Assert.Contains(demoApps, a => a.MatchScore >= 80);
            Assert.Contains(demoApps, a => a.MatchScore < 20);

            Assert.DoesNotContain(await db.ApplicationNotes.Where(n => n.UserId == demoId).ToListAsync(), n => n.Text == "stale note");

            // Three pending inbox suggestions (Interview with a time, Offer, Rejected) against seeded applications; no accumulation.
            var suggestions = await db.StatusSuggestions.Where(x => x.UserId == demoId).Include(x => x.Application).ToListAsync();
            Assert.Equal(3, suggestions.Count);
            Assert.All(suggestions, x => Assert.Equal(SuggestionState.Pending, x.Status));
            Assert.Contains(suggestions, x => x.SuggestedStatus == ApplicationStatus.Interview && x.InterviewAt.HasValue && x.Application!.Status == ApplicationStatus.Applied);
            Assert.Contains(suggestions, x => x.SuggestedStatus == ApplicationStatus.Offer && x.Application!.Status == ApplicationStatus.Interview);
            Assert.Contains(suggestions, x => x.SuggestedStatus == ApplicationStatus.Rejected && x.Application!.Status == ApplicationStatus.Applied);
            Assert.All(suggestions, x => Assert.Contains(x.Application!, demoApps.Where(a => a.Id == x.ApplicationId)));
            Assert.True(await db.ApplicationNotes.CountAsync(n => n.UserId == demoId) >= 2);
            var letter = await db.GeneratedCoverLetters.SingleAsync(c => c.UserId == demoId);
            Assert.True(letter.IsActive);
            Assert.Contains(demoApps, a => a.Id == letter.JobApplicationId);

            // Profile and the active resume survive; the visitor's extra resume version is gone and the
            // seeder's own second version ("General") takes its place, with the applications split between them.
            var demoProfile = await db.UserProfiles.SingleAsync(p => p.UserId == demoId);
            Assert.Equal(DemoSeeder.DisplayName, demoProfile.DisplayName);
            Assert.Equal("Demo User", demoProfile.DisplayName);
            // The profile row survives the purge, but every field the seeder owns is restored rather than
            // left as a visitor edited it — otherwise the page shows placeholder text for a name and no skills.
            Assert.Equal(DemoSeeder.FullName, demoProfile.FullName);
            Assert.Equal(DemoSeeder.Country,  demoProfile.Country);
            Assert.Equal(DemoSeeder.TargetRoles, ProfileTags.FromJson(demoProfile.TargetRolesJson));   // replaced, not merged
            Assert.Equal(DemoSeeder.Skills, ProfileTags.FromJson(demoProfile.SkillsJson));             // the visitor's "C#" is gone
            var resumes = await db.ResumeVersions.Where(r => r.UserId == demoId).OrderBy(r => r.Id).ToListAsync();
            Assert.Equal(2, resumes.Count);
            Assert.DoesNotContain(resumes, r => r.OriginalFileName == "visitor.pdf");
            var primary   = Assert.Single(resumes, r => r.IsActive);
            var secondary = Assert.Single(resumes, r => !r.IsActive);
            Assert.Equal("r.pdf", primary.OriginalFileName);
            Assert.Equal(DemoSeeder.PrimaryResumeLabel,   primary.Label);
            Assert.Equal(DemoSeeder.SecondaryResumeLabel, secondary.Label);
            Assert.Equal(3,  demoApps.Count(a => a.ResumeVersionId == secondary.Id));
            Assert.Equal(12, demoApps.Count(a => a.ResumeVersionId == primary.Id));

            var analytics = ResumeAnalyticsService.Build(resumes, demoApps);
            Assert.True(analytics.ShowCard);
            Assert.Equal($"{DemoSeeder.PrimaryResumeLabel} has the best response rate so far (63% across 8 applications).", analytics.Takeaway);
            Assert.True(analytics.ByResumeId[secondary.Id].LowConfidence);

            // Other users are untouched.
            Assert.Single(await db.JobApplications.Where(a => a.UserId == otherId).ToListAsync());
        }
    }

    [Fact]
    public async Task Seeder_is_a_no_op_when_the_demo_account_does_not_exist()
    {
        using var parent  = new TestAppFactory();
        using var factory = parent.WithWebHostBuilder(b => b.UseSetting("Demo:Email", "nobody@example.test"));
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().ResetAsync();
        Assert.False(result.UserFound);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().JobApplications.CountAsync());
    }

    [Fact]
    public async Task Admin_endpoint_is_hidden_from_non_admins_and_works_for_the_admin_regardless_of_auto_reset()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@example.test";
        var demoEmail  = $"demo-{Guid.NewGuid():N}@example.test";
        using var parent  = new TestAppFactory();
        using var factory = parent.WithWebHostBuilder(b =>
        {
            b.UseSetting("Admin:Email", adminEmail);
            b.UseSetting("Demo:Email", demoEmail);
            b.UseSetting("Demo:Password", "x-Demo-1!");
            b.UseSetting("Demo:AutoReset", "false");
        });

        // Demo account exists but is not the admin → 404 on both verbs.
        var demo = Client(factory);
        await Http.RegisterAsync(demo, demoEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await demo.GetAsync("/Admin/ResetDemo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await demo.PostAsync("/Admin/ResetDemo", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);

        // Anonymous → redirected to login.
        Assert.Equal(HttpStatusCode.Redirect, (await Client(factory).GetAsync("/Admin/ResetDemo")).StatusCode);

        // Admin → page renders, POST reseeds and redirects back with a toast.
        var admin = Client(factory);
        await Http.RegisterAsync(admin, adminEmail);
        var page = await admin.GetAsync("/Admin/ResetDemo");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Nightly reset off", await page.Content.ReadAsStringAsync());

        var token = await Http.GetAntiforgeryTokenAsync(admin, "/Admin/ResetDemo");
        var post = await admin.PostAsync("/Admin/ResetDemo", new FormUrlEncodedContent(new Dictionary<string, string> { ["confirm"] = "true", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        var after = await (await admin.GetAsync("/Admin/ResetDemo")).Content.ReadAsStringAsync();
        Assert.Contains("Demo account reset: 15 applications", after);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
        var demoId = (await users.FindByEmailAsync(demoEmail))!.Id;
        Assert.Equal(15, await db.JobApplications.CountAsync(a => a.UserId == demoId));
        Assert.Equal(0, await db.JobApplications.CountAsync(a => a.UserId != demoId));   // admin's own data untouched
    }

    [Fact]
    public async Task Admin_endpoint_is_disabled_when_no_admin_email_is_configured()
    {
        using var factory = new TestAppFactory();
        var client = Client(factory);
        await Http.RegisterAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Admin/ResetDemo")).StatusCode);
    }

    // ── The nightly reseed and a new session agree ──────────────────────────

    /// <summary>
    /// Everything the demo's fixed starting state covers, rendered as one comparable string: profile
    /// fields, drafts, practice rows, inbox suggestions, the three applications they point at, and the
    /// notes on those applications. Ids and timestamps are left out on purpose — they legitimately
    /// differ between two resets; the content must not.
    /// </summary>
    private static async Task<string> DemoState(WebApplicationFactory<Program> factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var lines = new List<string>();

        var p = await db.UserProfiles.AsNoTracking().SingleAsync(x => x.UserId == userId);
        lines.Add($"profile|{p.DisplayName}|{p.FullName}|{p.Country}|{p.Field}|{p.FieldCategory}|{p.Seniority}|{p.YearsExperience}|{p.Location}|{p.SkillsJson}|{p.TargetRolesJson}|{p.ProfileLastEnrichedAt}");

        var active = await db.ResumeVersions.AsNoTracking().Where(r => r.UserId == userId && r.IsActive).Select(r => (int?)r.Id).FirstOrDefaultAsync();
        foreach (var d in await db.ParsedResumes.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Id).ToListAsync())
            lines.Add($"draft|applied={d.Applied}|onActiveResume={d.ResumeVersionId == active}|{d.RawJson}");

        foreach (var q in await db.PracticeQuestions.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Prompt).ToListAsync())
            lines.Add($"practice|{q.Prompt}|{q.Topic}|{q.Difficulty}|{q.Category}|{q.Source}|{q.UserAnswer}|{q.AiFeedback}|{q.Score}|{q.AnsweredInSeconds}|saved={q.IsSaved}|{q.PriorAttemptsJson}|app={q.ApplicationId}");

        var apps = await db.JobApplications.AsNoTracking()
            .Where(a => a.UserId == userId && DemoSeeder.SuggestionCompanies.Contains(a.CompanyName)).ToListAsync();
        foreach (var a in apps.OrderBy(a => a.CompanyName, StringComparer.Ordinal))
        {
            lines.Add($"app|{a.CompanyName}|{a.RoleTitle}|{a.Status}|{a.InterviewAt:O}");
            var notes = await db.ApplicationNotes.AsNoTracking().Where(n => n.JobApplicationId == a.Id).Select(n => n.Text).ToListAsync();
            foreach (var n in notes.OrderBy(t => t, StringComparer.Ordinal)) lines.Add($"  note|{n}");
        }

        foreach (var sg in (await db.StatusSuggestions.AsNoTracking().Include(x => x.Application).Where(x => x.UserId == userId).ToListAsync())
                     .OrderBy(x => x.GmailMessageId, StringComparer.Ordinal))
            lines.Add($"suggestion|{sg.GmailMessageId}|{sg.Application?.CompanyName}|{sg.SuggestedStatus}|{sg.Status}|{sg.InterviewAt:O}|{sg.Summary}");

        return string.Join("\n", lines);
    }

    [Fact]
    public async Task The_nightly_reseed_and_a_new_demo_session_leave_the_same_starting_state()
    {
        // Two resets write the demo's starting state: DemoSeeder nightly, DemoProfileReset at every demo
        // sign-in. If they ever disagree, what a recruiter sees depends on the time of day. This pins them
        // to one state: reseed, let a "visitor" dirty everything the state covers, start a new session,
        // and require the result to be identical to the reseed.
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        const string demoPassword = "x-Demo-1!";
        using var parent  = new TestAppFactory();
        using var factory = parent.WithWebHostBuilder(b => { b.UseSetting("Demo:Email", demoEmail); b.UseSetting("Demo:Password", demoPassword); });

        await Http.RegisterAsync(Client(factory), demoEmail, demoPassword);
        string demoId;
        using (var scope = factory.Services.CreateScope())
            demoId = (await scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>().FindByEmailAsync(demoEmail))!.Id;

        using (var scope = factory.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<DemoSeeder>().ResetAsync()).UserFound);

        var nightly = await DemoState(factory, demoId);

        // The fixed state is really there, not two empty states agreeing.
        Assert.Contains("practice|A checkout API gets slow", nightly);
        Assert.Contains("draft|applied=False|onActiveResume=True|", nightly);
        Assert.Equal(3, nightly.Split('\n').Count(l => l.StartsWith("suggestion|") && l.Contains("|Pending|")));

        // A visitor's session: accept one suggestion, dismiss another, answer and star their own question,
        // retry the fixed one, apply the draft, change the profile.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var suggestions = scope.ServiceProvider.GetRequiredService<InternTrackAI.Services.Gmail.SuggestionService>();
            var pending = await db.StatusSuggestions.Where(x => x.UserId == demoId).OrderBy(x => x.GmailMessageId).ToListAsync();
            Assert.NotNull(await suggestions.AcceptAsync(demoId, pending.Single(x => x.GmailMessageId == "demo-shopify-offer").Id));
            Assert.NotNull(await suggestions.AcceptAsync(demoId, pending.Single(x => x.GmailMessageId == "demo-airbnb-interview").Id));
            Assert.NotNull(await suggestions.DismissAsync(demoId, pending.Single(x => x.GmailMessageId == "demo-cloudflare-rejected").Id));

            var fixedQuestion = await db.PracticeQuestions.SingleAsync(q => q.UserId == demoId);
            fixedQuestion.PriorAttemptsJson = "[{\"answer\":\"a visitor's retry\",\"score\":2}]";
            fixedQuestion.UserAnswer = "A visitor's own answer to the fixed question.";
            fixedQuestion.IsSaved = true;
            db.PracticeQuestions.Add(new PracticeQuestion
            {
                UserId = demoId, Prompt = "A visitor's generated question?", Topic = "visitor topic",
                PromptHash = QuestionHash.Of("A visitor's generated question?"), CreatedAt = DateTime.UtcNow, Source = QuestionSource.Practice
            });

            (await db.ParsedResumes.SingleAsync(d => d.UserId == demoId)).Applied = true;

            var profile = await db.UserProfiles.SingleAsync(p => p.UserId == demoId);
            profile.Field = "Registered Nursing";
            profile.FieldCategory = FieldCategory.Healthcare;
            profile.ProfileLastEnrichedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.NotEqual(nightly, await DemoState(factory, demoId));   // the visitor really changed it

        // A new demo session.
        var visitor = Client(factory);
        var token = await Http.GetAntiforgeryTokenAsync(visitor, "/");
        var res = await visitor.PostAsync("/Account/DemoLogin",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        Assert.Equal(nightly, await DemoState(factory, demoId));
    }
}
