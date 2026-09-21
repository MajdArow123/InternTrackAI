using System.Net;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static InternTrackAI.Tests.Integration.ResumeUploadTests;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The shared demo account's profile heals itself between visitors, so applying the nursing review
/// can't leave the next person looking at a nurse who has applied to Stripe.
/// <para>
/// The balance being pinned: the visitor who applied <b>keeps</b> their result — seeing the merge
/// land is the payoff of the whole flow — while a new sign-in and any other browser get the seeded
/// profile back. And the reset stays <b>narrow</b>: applications, notes and resumes are not touched,
/// because this runs on a plain page view and a full reseed there would rebuild the board under
/// someone mid-click.
/// </para>
/// </summary>
public class DemoProfileResetTests
{
    /// <summary>A host whose demo account is a real registered user, with the parser scripted.</summary>
    private sealed class DemoHarness : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public FakeProfileExtractor Extractor { get; } = new();
        public string Email { get; }
        public const string Password = "Integration-Pass-1!";

        public DemoHarness()
        {
            Email = $"demo-{Guid.NewGuid():N}@example.test";
            Factory = Parent.WithWebHostBuilder(b =>
            {
                b.UseSetting("Demo:Email", Email);
                b.UseSetting("Demo:Password", Password);
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IProfileExtractor>();
                    services.AddSingleton<IProfileExtractor>(Extractor);
                });
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public async Task<string> UserId()
        {
            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            return (await users.FindByEmailAsync(Email))!.Id;
        }

        public async Task<UserProfile> Profile()
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.UserProfiles.AsNoTracking().SingleAsync(p => p.UserId == UserIdCache);
        }

        public string UserIdCache { get; set; } = "";

        public async Task<int> DraftCount()
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ParsedResumes.CountAsync(p => p.UserId == UserIdCache);
        }

        /// <summary>Signs a fresh browser into the demo account the way the landing page's button does.</summary>
        public async Task<HttpClient> SignIn()
        {
            var client = Client();
            var token = await Http.GetAntiforgeryTokenAsync(client, "/");
            var res = await client.PostAsync("/Account/DemoLogin",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            return client;
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    /// <summary>Registers the demo user and gives it the seeded profile fields to start from.</summary>
    private static async Task<DemoHarness> Ready()
    {
        var h = new DemoHarness();
        var registrar = h.Client();
        await Http.RegisterAsync(registrar, h.Email, DemoHarness.Password);
        h.UserIdCache = await h.UserId();

        using (var scope = h.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.UserProfiles.SingleAsync(p => p.UserId == h.UserIdCache);
            DemoProfileReset.Apply(profile);
            await db.SaveChangesAsync();
        }

        return h;
    }

    /// <summary>Uploads and applies the canned nursing review as this browser.</summary>
    private static async Task ApplyNursingReview(DemoHarness h, HttpClient client)
    {
        await Upload(client, TestPdf.SampleResume());

        int draftId;
        using (var scope = h.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            draftId = await db.ParsedResumes.Where(p => p.UserId == h.UserIdCache && !p.Applied)
                .OrderByDescending(p => p.Id).Select(p => p.Id).FirstAsync();
        }

        var demo = ProfileExtractorService.DemoParse();
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["DraftId"] = draftId.ToString(),
            ["Field"] = demo.Field!,
            ["FieldCategory"] = demo.Category!.ToString()!,
        };
        for (var i = 0; i < demo.Skills.Count; i++)
        {
            form[$"Skills[{i}].Name"] = demo.Skills[i].Name;
            form[$"Skills[{i}].Selected"] = "true";
        }

        var res = await client.PostAsync("/Profile/ApplyResumeReview", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    // ── The applying visitor keeps their result ──────────────────────────────

    [Fact]
    public async Task The_visitor_who_applied_still_sees_it_on_the_profile_they_land_on()
    {
        // Without this, Apply would appear to do nothing and the demo would misrepresent the feature.
        using var h = await Ready();
        var visitor = await h.SignIn();

        await ApplyNursingReview(h, visitor);
        var html = await ProfilePage(visitor);

        Assert.Contains("Patient Assessment", html);
        Assert.Contains("Registered Nursing", html);
        Assert.Equal(FieldCategory.Healthcare, (await h.Profile()).FieldCategory);
    }

    [Fact]
    public async Task Reloading_the_profile_in_the_same_browser_does_not_undo_it()
    {
        using var h = await Ready();
        var visitor = await h.SignIn();
        await ApplyNursingReview(h, visitor);

        await ProfilePage(visitor);
        await ProfilePage(visitor);
        var html = await ProfilePage(visitor);

        Assert.Contains("Patient Assessment", html);
    }

    // ── Everyone else heals ──────────────────────────────────────────────────

    [Fact]
    public async Task Another_browser_already_signed_in_heals_on_its_next_profile_view()
    {
        // The case DemoLogin alone can't cover: two visitors share the account, the second signed in
        // *before* the first applied, so only /Profile is left to put it right.
        using var h = await Ready();
        var bystander = await h.SignIn();
        var applier   = await h.SignIn();

        await ApplyNursingReview(h, applier);
        Assert.Equal(FieldCategory.Healthcare, (await h.Profile()).FieldCategory);

        var html = await ProfilePage(bystander);

        Assert.DoesNotContain("Patient Assessment", html);
        Assert.Contains(DemoSeeder.Skills[0], html);

        var profile = await h.Profile();
        Assert.Equal(DemoSeeder.Category, profile.FieldCategory);
        Assert.Equal(DemoSeeder.Field, profile.Field);
        Assert.Equal(DemoSeeder.Skills, ProfileTags.FromJson(profile.SkillsJson));
        Assert.Null(profile.ProfileLastEnrichedAt);
    }

    [Fact]
    public async Task Signing_in_again_starts_from_the_seeded_profile()
    {
        using var h = await Ready();
        var first = await h.SignIn();
        await ApplyNursingReview(h, first);

        var second = await h.SignIn();   // a new visitor pressing "Try the live demo"

        var profile = await h.Profile();
        Assert.Equal(DemoSeeder.Skills, ProfileTags.FromJson(profile.SkillsJson));
        Assert.Equal(DemoSeeder.TargetRoles, ProfileTags.FromJson(profile.TargetRolesJson));
        Assert.Equal(DemoSeeder.Field, profile.Field);
        Assert.Null(profile.ProfileLastEnrichedAt);
        Assert.DoesNotContain("Patient Assessment", await ProfilePage(second));
    }

    [Fact]
    public async Task A_stranded_draft_does_not_greet_the_next_visitor()
    {
        // An unapplied draft from someone else would show "Resume analysis waiting for you" for a
        // review this visitor never ran.
        using var h = await Ready();
        var first = await h.SignIn();
        await Upload(first, TestPdf.SampleResume());
        Assert.Equal(1, await h.DraftCount());

        var second = await h.SignIn();

        Assert.Equal(0, await h.DraftCount());
        Assert.DoesNotContain("Resume analysis waiting for you", await ProfilePage(second));
    }

    // ── Narrow, not a reseed ─────────────────────────────────────────────────

    [Fact]
    public async Task The_reset_leaves_applications_notes_and_resumes_alone()
    {
        // This runs on a page view. Rebuilding the board here would yank it out from under a visitor
        // mid-click, which is why it is not a DemoSeeder.ResetAsync.
        using var h = await Ready();
        var first = await h.SignIn();
        await ApplyNursingReview(h, first);

        using (var scope = h.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.JobApplications.Add(new JobApplication
            {
                UserId = h.UserIdCache, CompanyName = "Stripe", RoleTitle = "Backend Intern", Status = ApplicationStatus.Applied
            });
            await db.SaveChangesAsync();
        }

        await h.SignIn();   // triggers the narrow reset

        using (var scope = h.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await db.JobApplications.CountAsync(a => a.UserId == h.UserIdCache));
            Assert.Equal(1, await db.ResumeVersions.CountAsync(r => r.UserId == h.UserIdCache));
        }
    }

    // ── Everyone else is untouched ───────────────────────────────────────────

    [Fact]
    public async Task A_normal_account_is_never_reset()
    {
        // The heal is gated on the demo account. A real user who applies a review keeps it forever,
        // which is the entire point of the feature everywhere except this one shared login.
        using var h = await Ready();

        var normal = h.Client();
        var email = await Http.RegisterAsync(normal);

        string normalId;
        using (var scope = h.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            normalId = (await users.FindByEmailAsync(email))!.Id;
        }

        await Upload(normal, TestPdf.SampleResume());

        int draftId;
        using (var scope = h.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            draftId = await db.ParsedResumes.Where(p => p.UserId == normalId && !p.Applied)
                .OrderByDescending(p => p.Id).Select(p => p.Id).FirstAsync();
        }

        var token = await Http.GetAntiforgeryTokenAsync(normal, "/Profile");
        var res = await normal.PostAsync("/Profile/ApplyResumeReview", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["DraftId"] = draftId.ToString(),
            ["Field"] = "Registered Nursing",
            ["FieldCategory"] = "Healthcare",
            ["Skills[0].Name"] = "Patient Assessment",
            ["Skills[0].Selected"] = "true",
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        await ProfilePage(normal);
        await ProfilePage(normal);   // the heal would have fired by now if it applied to real accounts

        using var check = h.Factory.Services.CreateScope();
        var db2 = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db2.UserProfiles.AsNoTracking().SingleAsync(p => p.UserId == normalId);
        Assert.Contains("Patient Assessment", ProfileTags.FromJson(profile.SkillsJson));
        Assert.Equal("Registered Nursing", profile.Field);
        Assert.NotNull(profile.ProfileLastEnrichedAt);
    }
}

/// <summary>
/// The pure half: the stamp that decides "was this browser the one that applied?".
/// </summary>
public class DemoProfileStampTests
{
    [Fact]
    public void Sub_second_precision_is_dropped_so_a_database_round_trip_cannot_change_it()
    {
        // SQLite keeps sub-millisecond precision and PostgreSQL's timestamptz rounds to microseconds.
        // Comparing raw ticks would make the applying visitor reset their own result on Postgres only.
        var written = new DateTime(2026, 9, 20, 19, 31, 53, DateTimeKind.Utc).AddTicks(6583700);
        var readBack = new DateTime(2026, 9, 20, 19, 31, 53, DateTimeKind.Utc).AddTicks(6583000);

        Assert.NotEqual(written.Ticks, readBack.Ticks);
        Assert.Equal(DemoProfileReset.Stamp(written), DemoProfileReset.Stamp(readBack));
    }

    [Fact]
    public void Different_seconds_produce_different_stamps()
    {
        var first  = new DateTime(2026, 9, 20, 19, 31, 53, DateTimeKind.Utc);

        Assert.NotEqual(DemoProfileReset.Stamp(first), DemoProfileReset.Stamp(first.AddSeconds(1)));
    }

    [Fact]
    public void Apply_restores_every_field_the_demo_owns_and_clears_the_enrichment_stamp()
    {
        var contaminated = new UserProfile
        {
            UserId = "u1",
            SkillsJson = ProfileTags.ToJson(new[] { "Patient Assessment" }),
            TargetRolesJson = ProfileTags.ToJson(new[] { "Nursing Student Placement" }),
            Field = "Registered Nursing",
            FieldCategory = FieldCategory.Healthcare,
            Seniority = SeniorityLevel.Senior,
            YearsExperience = 12,
            Location = "Vancouver, BC",
            FullName = "Jordan Lee",
            ProfileLastEnrichedAt = DateTime.UtcNow
        };

        DemoProfileReset.Apply(contaminated);

        Assert.Equal(DemoSeeder.Skills, ProfileTags.FromJson(contaminated.SkillsJson));
        Assert.Equal(DemoSeeder.TargetRoles, ProfileTags.FromJson(contaminated.TargetRolesJson));
        Assert.Equal(DemoSeeder.Field, contaminated.Field);
        Assert.Equal(DemoSeeder.Category, contaminated.FieldCategory);
        Assert.Equal(DemoSeeder.Seniority, contaminated.Seniority);
        Assert.Equal(DemoSeeder.YearsExperience, contaminated.YearsExperience);
        Assert.Equal(DemoSeeder.Location, contaminated.Location);
        Assert.Equal(DemoSeeder.FullName, contaminated.FullName);
        Assert.Null(contaminated.ProfileLastEnrichedAt);
    }
}
