using System.Net;
using System.Net.Http.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Services;
using InternTrackAI.Models.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Two users share one app instance. Bob owns one of everything (application, note, generated
/// cover letter, resume version, prep session). Alice is signed in and
/// tries every id-taking action with Bob's ids: each must answer 404 and leave Bob's data intact.
/// </summary>
public class OwnershipFixture : TestAppFactory, IAsyncLifetime
{
    public HttpClient Alice { get; private set; } = null!;
    public string AliceToken { get; private set; } = "";
    public string BobId { get; private set; } = "";
    public int BobAppId, BobNoteId, BobLetterId, BobResumeId, BobPrepId;

    public async Task InitializeAsync()
    {
        var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false };

        var bob = CreateClient(options);
        var bobEmail = await Http.RegisterAsync(bob);

        Alice = CreateClient(options);
        await Http.RegisterAsync(Alice);
        AliceToken = await Http.GetAntiforgeryTokenAsync(Alice, "/JobApplications/Create");

        using var scope = Services.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        BobId = (await users.FindByEmailAsync(bobEmail))!.Id;

        var app = new JobApplication { UserId = BobId, CompanyName = "Bob Corp", RoleTitle = "Bob Role", Status = ApplicationStatus.Applied, JobDescription = "Bob's private posting text." };
        db.JobApplications.Add(app);
        await db.SaveChangesAsync();
        BobAppId = app.Id;

        var note   = new ApplicationNote { UserId = BobId, JobApplicationId = app.Id, Text = "Bob's private note" };
        var letter = new GeneratedCoverLetter { UserId = BobId, JobApplicationId = app.Id, Content = "Bob's letter", CompanyName = "Bob Corp", RoleTitle = "Bob Role", IsActive = true, VersionNumber = 1 };
        var resume = new ResumeVersion { UserId = BobId, VersionNumber = 1, OriginalFileName = "bob.pdf", StoredPath = $"resumes/{BobId}/bob.pdf", IsActive = true };
        var prep   = new PracticeQuestion { UserId = BobId, ApplicationId = app.Id, Prompt = "Bob's question?", PromptHash = QuestionHash.Of("Bob's question?"), CreatedAt = DateTime.UtcNow };
        db.AddRange(note, letter, resume, prep);
        await db.SaveChangesAsync();
        BobNoteId = note.Id; BobLetterId = letter.Id; BobResumeId = resume.Id; BobPrepId = prep.Id;
    }

    public new Task DisposeAsync() { Dispose(); return Task.CompletedTask; }

    public FormUrlEncodedContent Form(params (string, string)[] fields)
    {
        var d = fields.ToDictionary(f => f.Item1, f => f.Item2);
        d["__RequestVerificationToken"] = AliceToken;
        return new FormUrlEncodedContent(d);
    }

    public HttpRequestMessage JsonPost(string url, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("RequestVerificationToken", AliceToken);
        return req;
    }
}

public class OwnershipTests : IClassFixture<OwnershipFixture>
{
    private readonly OwnershipFixture _f;
    public OwnershipTests(OwnershipFixture f) => _f = f;

    private static async Task Assert404(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Bob", body);    // nothing of Bob's leaks into the 404 page/body
    }

    // ── JobApplications ──
    [Fact] public async Task JobApplications_Edit_GET()   => await Assert404(await _f.Alice.GetAsync($"/JobApplications/Edit/{_f.BobAppId}"));
    [Fact] public async Task JobApplications_Delete_GET() => await Assert404(await _f.Alice.GetAsync($"/JobApplications/Delete/{_f.BobAppId}"));
    [Fact] public async Task JobApplications_Notes_GET()  => await Assert404(await _f.Alice.GetAsync($"/JobApplications/Notes?appId={_f.BobAppId}"));
    [Fact] public async Task JobApplications_KeywordCoverage_POST() => await Assert404(await _f.Alice.PostAsync("/JobApplications/KeywordCoverage", _f.Form(("AppId", _f.BobAppId.ToString()))));

    [Fact]
    public async Task JobApplications_Edit_POST_cannot_overwrite_or_take_over()
    {
        var res = await _f.Alice.PostAsync($"/JobApplications/Edit/{_f.BobAppId}", _f.Form(
            ("Id", _f.BobAppId.ToString()), ("CompanyName", "Hijacked"), ("RoleTitle", "Hijacked"), ("Status", "Saved"), ("WorkMode", "Remote")));
        await Assert404(res);
        await AssertBobAppUntouched();
    }

    [Fact]
    public async Task JobApplications_MarkContacted_and_Snooze_POST()
    {
        await Assert404(await _f.Alice.PostAsync($"/JobApplications/{_f.BobAppId}/contacted", _f.Form()));
        await Assert404(await _f.Alice.PostAsync($"/JobApplications/{_f.BobAppId}/snooze", _f.Form()));
        // AJAX flavour answers JSON 404 without leaking anything either
        var ajax = new HttpRequestMessage(HttpMethod.Post, $"/JobApplications/{_f.BobAppId}/contacted");
        ajax.Headers.Add("RequestVerificationToken", _f.AliceToken);
        ajax.Headers.Add("X-Requested-With", "XMLHttpRequest");
        await Assert404(await _f.Alice.SendAsync(ajax));
        await AssertBobAppUntouched();
    }

    [Fact]
    public async Task JobApplications_Delete_POST()
    {
        await Assert404(await _f.Alice.PostAsync($"/JobApplications/Delete/{_f.BobAppId}", _f.Form(("Id", _f.BobAppId.ToString()))));
        await AssertBobAppUntouched();
    }

    [Fact]
    public async Task JobApplications_AddNote_POST()
    {
        await Assert404(await _f.Alice.PostAsync("/JobApplications/AddNote", _f.Form(("appId", _f.BobAppId.ToString()), ("text", "intruder"))));
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.ApplicationNotes.CountAsync(n => n.JobApplicationId == _f.BobAppId));
    }

    [Fact]
    public async Task JobApplications_BulkDelete_and_BulkStatus_ignore_foreign_ids()
    {
        var del = await _f.Alice.PostAsync("/JobApplications/BulkDelete", _f.Form(("ids", _f.BobAppId.ToString())));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        var st = await _f.Alice.PostAsync("/JobApplications/BulkStatus", _f.Form(("ids", _f.BobAppId.ToString()), ("newStatus", "Rejected")));
        Assert.Equal(HttpStatusCode.Redirect, st.StatusCode);
        await AssertBobAppUntouched();
    }

    [Fact]
    public async Task JobApplications_Export_and_Index_only_contain_own_rows()
    {
        var csv = await (await _f.Alice.GetAsync("/JobApplications/Export")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Bob Corp", csv);
        var html = await (await _f.Alice.GetAsync("/JobApplications")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Bob Corp", html);            // compare modal only reads rows rendered here
        Assert.DoesNotContain($"data-app-id=\"{_f.BobAppId}\"", html);
    }

    // ── Follow-up drafts ──
    [Fact]
    public async Task FollowUp_Generate_and_Improve_POST()
    {
        await Assert404(await _f.Alice.SendAsync(_f.JsonPost($"/JobApplications/{_f.BobAppId}/followup", new { })));
        await Assert404(await _f.Alice.SendAsync(_f.JsonPost($"/JobApplications/{_f.BobAppId}/followup/improve", new { subject = "Hi", body = "Draft text", instruction = "shorter" })));
        await AssertBobAppUntouched();
    }

    // ── CoverLetter ──
    [Fact] public async Task CoverLetter_Generate_GET_with_foreign_appId() => await Assert404(await _f.Alice.GetAsync($"/CoverLetter/Generate?appId={_f.BobAppId}"));
    [Fact] public async Task CoverLetter_Download_GET()  => await Assert404(await _f.Alice.GetAsync($"/CoverLetter/Download/{_f.BobLetterId}"));
    [Fact] public async Task CoverLetter_GenerateAjax_POST() => await Assert404(await _f.Alice.SendAsync(_f.JsonPost("/CoverLetter/GenerateAjax", new { applicationId = _f.BobAppId, extraNotes = "" })));
    [Fact] public async Task CoverLetter_ImproveAjax_POST()  => await Assert404(await _f.Alice.SendAsync(_f.JsonPost("/CoverLetter/ImproveAjax", new { content = "Some letter text", applicationId = _f.BobAppId, instructions = "shorter" })));

    [Fact]
    public async Task CoverLetter_Save_POST_cannot_link_to_foreign_application()
    {
        await Assert404(await _f.Alice.PostAsync("/CoverLetter/Save", _f.Form(("applicationId", _f.BobAppId.ToString()), ("content", "sneaky"), ("company", "x"), ("role", "y"))));
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.GeneratedCoverLetters.CountAsync(c => c.JobApplicationId == _f.BobAppId));
    }

    [Fact]
    public async Task CoverLetter_Delete_POST()
    {
        await Assert404(await _f.Alice.PostAsync("/CoverLetter/Delete", _f.Form(("id", _f.BobLetterId.ToString()))));
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.NotNull(await db.GeneratedCoverLetters.FindAsync(_f.BobLetterId));
    }

    [Fact]
    public async Task CoverLetter_SetActive_POST()
    {
        await Assert404(await _f.Alice.PostAsync("/CoverLetter/SetActive", _f.Form(("id", _f.BobLetterId.ToString()))));
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True((await db.GeneratedCoverLetters.FindAsync(_f.BobLetterId))!.IsActive);
    }

    // ── InterviewPrep ──
    [Fact] public async Task InterviewPrep_Prep_GET()          => await Assert404(await _f.Alice.GetAsync($"/InterviewPrep/Prep?appId={_f.BobAppId}"));
    [Fact] public async Task InterviewPrep_Generate_POST()     => await Assert404(await _f.Alice.SendAsync(_f.JsonPost("/InterviewPrep/Generate", new { appId = _f.BobAppId })));
    [Fact] public async Task InterviewPrep_Critique_POST()     => await Assert404(await _f.Alice.SendAsync(_f.JsonPost("/InterviewPrep/CritiqueAnswer", new { appId = _f.BobAppId, question = "Q?", answer = LongEnoughAnswer })));

    // ── Practice ──
    // The answer clears PracticeAnswerService.MinAnswerChars on purpose: the length rule is checked
    // first, so a short answer would be rejected before ownership was ever tested.
    [Fact] public async Task Practice_SubmitAnswer_POST()       => await Assert404(await _f.Alice.PostAsync("/Practice/SubmitAnswer", _f.Form(("questionId", _f.BobPrepId.ToString()), ("answer", LongEnoughAnswer))));
    [Fact] public async Task Practice_ToggleSaved_POST()        => await Assert404(await _f.Alice.PostAsync("/Practice/ToggleSaved", _f.Form(("questionId", _f.BobPrepId.ToString()))));

    private const string LongEnoughAnswer = "An answer long enough to be worth sending to the grader at all.";

    // ── Profile documents ──
    [Fact] public async Task Profile_DownloadResume_GET()      => await Assert404(await _f.Alice.GetAsync($"/Profile/DownloadResume/{_f.BobResumeId}"));

    [Fact]
    public async Task Profile_SetActiveResume_POST()
    {
        await Assert404(await _f.Alice.PostAsync("/Profile/SetActiveResume", _f.Form(("id", _f.BobResumeId.ToString()))));
        await AssertBobDocsUntouched();
    }

    [Fact]
    public async Task Profile_DeleteResume_POST()
    {
        await Assert404(await _f.Alice.PostAsync("/Profile/DeleteResume", _f.Form(("id", _f.BobResumeId.ToString()))));
        await AssertBobDocsUntouched();
    }

    // ── helpers ──
    private async Task AssertBobAppUntouched()
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var app = await db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == _f.BobAppId);
        Assert.Equal(_f.BobId, app.UserId);
        Assert.Equal("Bob Corp", app.CompanyName);
        Assert.Equal(ApplicationStatus.Applied, app.Status);
        Assert.Equal(1, await db.GeneratedCoverLetters.CountAsync(c => c.JobApplicationId == _f.BobAppId));
        Assert.Equal(1, await db.PracticeQuestions.CountAsync(q => q.ApplicationId == _f.BobAppId));
    }

    private async Task AssertBobDocsUntouched()
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True((await db.ResumeVersions.AsNoTracking().SingleAsync(r => r.Id == _f.BobResumeId)).IsActive);
    }
}
