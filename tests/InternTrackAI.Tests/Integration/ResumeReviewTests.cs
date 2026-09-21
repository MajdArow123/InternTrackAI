using System.Net;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static InternTrackAI.Tests.Integration.ResumeUploadTests;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The review screen — the only path from an AI parse to the profile.
/// <para>
/// The guarantees here are the feature: <b>nothing is written until the form is posted</b>, an
/// unchecked skill is never written, discarding leaves the profile byte-identical, and a tag the user
/// already had is never removed. Each one is a test because each one is a way the old silent-merge
/// behaviour could come back.
/// </para>
/// </summary>
public class ResumeReviewTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Posts the review form. Only the rows named in <paramref name="keepSkills"/> are ticked.</summary>
    private static async Task<HttpResponseMessage> Apply(
        HttpClient client, int draftId,
        IEnumerable<string> allSkills, IEnumerable<string> keepSkills,
        IEnumerable<string>? allRoles = null, IEnumerable<string>? keepRoles = null,
        IDictionary<string, string>? scalars = null)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["DraftId"] = draftId.ToString()
        };

        if (scalars is not null)
            foreach (var (k, v) in scalars) form[k] = v;

        var keep = new HashSet<string>(keepSkills, StringComparer.OrdinalIgnoreCase);
        var skills = allSkills.ToList();
        for (var i = 0; i < skills.Count; i++)
        {
            form[$"Skills[{i}].Name"] = skills[i];
            // An unchecked box posts no value at all, exactly as a browser would send it.
            if (keep.Contains(skills[i])) form[$"Skills[{i}].Selected"] = "true";
        }

        var roles = (allRoles ?? Array.Empty<string>()).ToList();
        var keepR = new HashSet<string>(keepRoles ?? roles, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < roles.Count; i++)
        {
            form[$"Roles[{i}].Name"] = roles[i];
            if (keepR.Contains(roles[i])) form[$"Roles[{i}].Selected"] = "true";
        }

        return await client.PostAsync("/Profile/ApplyResumeReview", new FormUrlEncodedContent(form));
    }

    private static async Task<HttpResponseMessage> Discard(HttpClient client, int draftId)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        return await client.PostAsync("/Profile/DiscardResumeReview", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["draftId"] = draftId.ToString()
        }));
    }

    private static async Task<string> ReviewPage(HttpClient client, int draftId)
    {
        var res = await client.GetAsync($"/Profile/ReviewResume?id={draftId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    private static readonly string[] DefaultSkills = { "Python", "React", "SQL", FakeProfileExtractor.LowConfidenceSkill };
    private static readonly string[] DefaultRoles  = { "Backend Developer Intern" };

    // ── The screen ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_review_screen_shows_every_skill_with_the_evidence_behind_it()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Upload(client, TestPdf.SampleResume());
        var draft = Assert.Single(await h.DraftsOf(userId));

        var html = await ReviewPage(client, draft.Id);

        Assert.Contains("Review what we found", html);
        Assert.Contains("Python", html);
        Assert.Contains("Built an ETL pipeline in Python processing 2M rows/day", html);   // the evidence
        Assert.Contains("Nothing you've already saved will be removed", html);             // the merge notice
        Assert.Contains("George Brown College", html);                                     // education, read-only
    }

    [Fact]
    public async Task A_low_confidence_skill_renders_unchecked_and_tagged()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Upload(client, TestPdf.SampleResume());
        var draft = Assert.Single(await h.DraftsOf(userId));

        var html = await ReviewPage(client, draft.Id);

        Assert.Contains("low confidence", html);

        // The low-confidence row is the only unchecked box on the page.
        var lowIndex = Array.IndexOf(DefaultSkills, FakeProfileExtractor.LowConfidenceSkill);
        Assert.Contains($"name=\"Skills[{lowIndex}].Selected\" value=\"true\"", html);
        Assert.DoesNotContain($"name=\"Skills[{lowIndex}].Selected\" value=\"true\" checked", html);
        Assert.Contains("name=\"Skills[0].Selected\" value=\"true\" checked", html);
    }

    // ── Applying ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Applying_writes_only_the_checked_rows()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Upload(client, TestPdf.SampleResume());
        var draft = Assert.Single(await h.DraftsOf(userId));

        var res = await Apply(client, draft.Id, DefaultSkills, new[] { "Python", "SQL" }, DefaultRoles, DefaultRoles,
            new Dictionary<string, string> { ["Field"] = "Software Engineering", ["FieldCategory"] = "Technology" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var profile = (await h.ProfileOf(userId))!;
        Assert.Equal(new[] { "Python", "SQL" }, Tags(profile.SkillsJson));
        Assert.DoesNotContain("React", Tags(profile.SkillsJson));
        Assert.DoesNotContain(FakeProfileExtractor.LowConfidenceSkill, Tags(profile.SkillsJson));
        Assert.Equal("Software Engineering", profile.Field);
        Assert.Equal(FieldCategory.Technology, profile.FieldCategory);
        Assert.NotNull(profile.ProfileLastEnrichedAt);

        Assert.True((await h.DraftsOf(userId)).Single().Applied);
        Assert.Contains("Profile updated", await ProfilePage(client));
    }

    [Fact]
    public async Task Applying_never_removes_a_tag_the_user_already_had()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await h.SeedProfile(userId, "Existing Name", new[] { "Rust", "React" }, new[] { "Data Science Intern" });
        await Upload(client, TestPdf.SampleResume());
        var draft = Assert.Single(await h.DraftsOf(userId));

        // Keep only Python: React is already stored and must survive being left unchecked here.
        await Apply(client, draft.Id, DefaultSkills, new[] { "Python" }, DefaultRoles, DefaultRoles);

        var profile = (await h.ProfileOf(userId))!;
        Assert.Equal(new[] { "Rust", "React", "Python" }, Tags(profile.SkillsJson));
        Assert.Equal(new[] { "Data Science Intern", "Backend Developer Intern" }, Tags(profile.TargetRolesJson));
    }

    [Fact]
    public async Task Applying_the_same_resume_twice_does_not_duplicate_a_tag()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await Upload(client, TestPdf.SampleResume());
        await Apply(client, (await h.DraftsOf(userId)).First().Id, DefaultSkills, DefaultSkills, DefaultRoles, DefaultRoles);

        await Upload(client, TestPdf.SampleResume(), "v2.pdf");
        await Apply(client, (await h.DraftsOf(userId)).First(d => !d.Applied).Id, DefaultSkills, DefaultSkills, DefaultRoles, DefaultRoles);

        var profile = (await h.ProfileOf(userId))!;
        Assert.Equal(DefaultSkills, Tags(profile.SkillsJson));
        Assert.Equal(DefaultRoles, Tags(profile.TargetRolesJson));
        Assert.Contains("Nothing new to add", await ProfilePage(client));
    }

    [Fact]
    public async Task Case_and_whitespace_variants_of_a_stored_tag_are_not_added_again()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await h.SeedProfile(userId, null, new[] { "React", "SQL" }, null);
        await Upload(client, TestPdf.SampleResume());
        var draft = Assert.Single(await h.DraftsOf(userId));

        // A stale client re-posts them differently cased and padded.
        var posted = new[] { "react", " SQL ", "Go" };
        await Apply(client, draft.Id, posted, posted);

        var profile = (await h.ProfileOf(userId))!;
        Assert.Equal(new[] { "React", "SQL", "Go" }, Tags(profile.SkillsJson));   // first-seen casing kept
    }

    [Fact]
    public async Task An_applied_draft_cannot_be_applied_a_second_time()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Upload(client, TestPdf.SampleResume());
        var draft = Assert.Single(await h.DraftsOf(userId));

        await Apply(client, draft.Id, DefaultSkills, new[] { "Python" });
        await Apply(client, draft.Id, DefaultSkills, DefaultSkills);   // a double submit or a back-button repost

        var profile = (await h.ProfileOf(userId))!;
        Assert.Equal(new[] { "Python" }, Tags(profile.SkillsJson));   // the second post added nothing
    }

    // ── Discarding ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Discarding_leaves_the_profile_byte_identical()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await h.SeedProfile(userId, "Existing Name", new[] { "Rust" }, new[] { "Data Science Intern" });

        var before = (await h.ProfileOf(userId))!;
        await Upload(client, TestPdf.SampleResume());
        var draft = Assert.Single(await h.DraftsOf(userId));

        var res = await Discard(client, draft.Id);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var after = (await h.ProfileOf(userId))!;
        Assert.Equal(before.FullName, after.FullName);
        Assert.Equal(before.SkillsJson, after.SkillsJson);
        Assert.Equal(before.TargetRolesJson, after.TargetRolesJson);
        Assert.Equal(before.Field, after.Field);
        Assert.Equal(before.FieldCategory, after.FieldCategory);
        Assert.Equal(before.Seniority, after.Seniority);
        Assert.Equal(before.Location, after.Location);
        Assert.Equal(before.YearsExperience, after.YearsExperience);
        Assert.Null(after.ProfileLastEnrichedAt);

        Assert.Empty(await h.DraftsOf(userId));
        Assert.Contains("your profile is unchanged", await ProfilePage(client));
    }

    // ── Getting back to a draft ──────────────────────────────────────────────

    [Fact]
    public async Task A_draft_left_unreviewed_is_offered_again_on_the_profile_page()
    {
        using var h = new Harness();
        var client = h.Client();
        await Http.RegisterAsync(client);
        await Upload(client, TestPdf.SampleResume());

        var html = await ProfilePage(client);

        Assert.Contains("Resume analysis waiting for you", html);
        Assert.Contains("Review what we found", html);
    }

    [Fact]
    public async Task Review_with_no_draft_redirects_instead_of_erroring()
    {
        using var h = new Harness();
        var client = h.Client();
        await Http.RegisterAsync(client);

        var res = await client.GetAsync("/Profile/ReviewResume");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("Nothing to review", await ProfilePage(client));
    }

    // ── Re-parse ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Re_parsing_the_active_resume_makes_a_new_draft_without_a_re_upload()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Upload(client, TestPdf.SampleResume());
        await Discard(client, (await h.DraftsOf(userId)).Single().Id);

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var res = await client.PostAsync("/Profile/ReparseResume",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Profile/ReviewResume", res.Headers.Location!.ToString());
        Assert.Single(await h.DraftsOf(userId));

        // The text was cached on the version at upload, so re-parsing re-reads no file.
        Assert.Equal(2, h.Extractor.Calls);
        Assert.Equal(h.Extractor.Texts[0], h.Extractor.Texts[1]);
    }

    [Fact]
    public async Task Re_parsing_with_no_active_resume_says_so_and_stores_nothing()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var res = await client.PostAsync("/Profile/ReparseResume",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(0, h.Extractor.Calls);
        Assert.Empty(await h.DraftsOf(userId));
        Assert.Contains("No active resume found", await ProfilePage(client));
    }

    // ── Ownership and pruning ────────────────────────────────────────────────

    [Fact]
    public async Task Another_users_draft_is_a_404_on_every_endpoint_that_takes_its_id()
    {
        using var h = new Harness();

        var owner = h.Client();
        var ownerId = await h.UserIdOf(await Http.RegisterAsync(owner));
        await Upload(owner, TestPdf.SampleResume());
        var draft = Assert.Single(await h.DraftsOf(ownerId));

        var stranger = h.Client();
        await Http.RegisterAsync(stranger);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/Profile/ReviewResume?id={draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Apply(stranger, draft.Id, DefaultSkills, DefaultSkills)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Discard(stranger, draft.Id)).StatusCode);

        // Nothing moved: the owner's draft is intact and unapplied.
        var after = Assert.Single(await h.DraftsOf(ownerId));
        Assert.False(after.Applied);
    }

    [Fact]
    public async Task Only_the_most_recent_drafts_are_kept()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        for (var i = 0; i < ResumeParseService.MaxDraftsPerUser + 2; i++)
            await Upload(client, TestPdf.SampleResume(), $"v{i}.pdf");

        Assert.Equal(ResumeParseService.MaxDraftsPerUser, (await h.DraftsOf(userId)).Count);
    }

    [Fact]
    public async Task Deleting_the_account_deletes_the_drafts_with_it()
    {
        // A draft holds a model's reading of a resume; leaving it behind leaves that behind.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await Upload(client, TestPdf.SampleResume());
        Assert.Single(await h.DraftsOf(userId));

        using (var scope = h.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserDataPurger>().PurgeAsync(userId);
        }

        Assert.Empty(await h.DraftsOf(userId));
    }

    // ── Demo account ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_demo_account_gets_a_nursing_parse_without_calling_the_model()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        using var h = new Harness(("Demo:Email", demoEmail), ("Demo:Password", "irrelevant-here-1!"));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client, demoEmail));

        var res = await Upload(client, TestPdf.SampleResume());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Profile/ReviewResume", res.Headers.Location!.ToString());

        Assert.Equal(0, h.Extractor.Calls);   // no model call, no permit
        var draft = Assert.Single(await h.DraftsOf(userId));

        var html = await ReviewPage(client, draft.Id);

        // The demo exists to show the app is not IT-only, so it must read as healthcare on sight.
        Assert.Contains("Registered Nursing", html);
        Assert.Contains("Healthcare", html);
        Assert.Contains("Patient Assessment", html);
        Assert.Contains("Nursing Student Placement", html);
        Assert.DoesNotContain("Software Engineering", html);
    }

    [Fact]
    public async Task The_demo_parse_keeps_its_low_confidence_skills_unchecked()
    {
        // The demo is where a visitor learns that shaky skills start unchecked. A prompt or seed change
        // that made every demo skill high-confidence would remove the thing it exists to demonstrate.
        var demo = ProfileExtractorService.DemoParse();

        var low = demo.Skills.Where(s => s.Confidence == SkillConfidence.Low).ToList();
        Assert.True(low.Count >= 2, $"expected at least 2 low-confidence demo skills, found {low.Count}");
        Assert.All(demo.Skills, s => Assert.False(string.IsNullOrWhiteSpace(s.Evidence)));
        Assert.Equal(FieldCategory.Healthcare, demo.Category);
    }

    [Fact]
    public async Task The_demo_account_can_apply_its_review_like_anyone_else()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        using var h = new Harness(("Demo:Email", demoEmail), ("Demo:Password", "irrelevant-here-1!"));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client, demoEmail));
        await Upload(client, TestPdf.SampleResume());
        var draft = Assert.Single(await h.DraftsOf(userId));

        var skills = new[] { "Patient Assessment", "Wound Care" };
        await Apply(client, draft.Id, skills, skills, null, null,
            new Dictionary<string, string> { ["Field"] = "Registered Nursing", ["FieldCategory"] = "Healthcare" });

        var profile = (await h.ProfileOf(userId))!;
        Assert.Equal(skills, Tags(profile.SkillsJson));
        Assert.Equal(FieldCategory.Healthcare, profile.FieldCategory);
    }
}
