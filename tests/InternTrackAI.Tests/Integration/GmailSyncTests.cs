using System.Net;
using System.Net.Http.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// <see cref="GmailSyncService.SyncAsync"/> against the real DbContext with a fake inbox, fake Google
/// and a scripted classifier: matching, de-duplication, same-status dropping, the rate-limit bucket,
/// token refresh and the "Sync now" endpoint.
/// </summary>
public class GmailSyncTests
{
    private sealed class Host : IDisposable
    {
        public TestAppFactory Parent;
        public WebApplicationFactory<Program> Factory;
        public FakeGoogleOAuthClient OAuth;
        public FakeGmailClient Gmail;
        public FakeStatusClassifier Classifier;
        public HttpClient Client = null!;
        public string UserId = "";

        public Host(params (string, string)[] settings)
        {
            (Parent, Factory, OAuth, Gmail, Classifier) = GmailTestHost.BootWithClassifier(true, settings);
        }

        /// <summary>Registers a user, connects Gmail through the real callback, and returns the user id.</summary>
        public async Task<Host> ConnectedAsync()
        {
            Client = GmailTestHost.Client(Factory);
            var email = await Http.RegisterAsync(Client);
            var start = await Client.GetAsync("/Integrations/Gmail/Connect");
            var state = System.Web.HttpUtility.ParseQueryString(new Uri(start.Headers.Location!.ToString()).Query)["state"]!;
            await Client.GetAsync($"/Integrations/Gmail/Callback?code=c&state={Uri.EscapeDataString(state)}");
            using var scope = Factory.Services.CreateScope();
            UserId = (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
            return this;
        }

        public async Task<int> AddAppAsync(string company, ApplicationStatus status, string role = "Intern", string? link = null, string? note = null)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication { UserId = UserId, CompanyName = company, RoleTitle = role, Status = status, JobLink = link };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            if (note is not null) { db.ApplicationNotes.Add(new ApplicationNote { UserId = UserId, JobApplicationId = app.Id, Text = note }); await db.SaveChangesAsync(); }
            return app.Id;
        }

        public async Task<GmailSyncResult> SyncAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<GmailSyncService>().SyncAsync(UserId);
        }

        public async Task<List<StatusSuggestion>> SuggestionsAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().StatusSuggestions.AsNoTracking().Where(s => s.UserId == UserId).OrderBy(s => s.Id).ToListAsync();
        }

        public async Task<GmailConnection> ConnectionAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().GmailConnections.AsNoTracking().SingleAsync(c => c.UserId == UserId);
        }

        public async Task SetConnectionAsync(Action<GmailConnection> mutate)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var c = await db.GmailConnections.SingleAsync(x => x.UserId == UserId);
            mutate(c);
            await db.SaveChangesAsync();
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static GmailMessage Mail(string id, string from, string subject, string body = "Hello,\n\nWe would like to move forward.\n") =>
        new(id, subject, from, DateTime.UtcNow.AddHours(-2), body.Length > 80 ? body[..80] : body, body);

    private static StatusClassification Says(ApplicationStatus? status, double confidence = 0.9, string summary = "Summary.", DateTime? at = null) =>
        new(true, status, confidence, summary, at);

    [Fact]
    public async Task Sender_domain_maps_mail_to_the_right_application_and_stores_a_suggestion_without_the_body()
    {
        using var h = await new Host().ConnectedAsync();
        var stripe  = await h.AddAppAsync("Stripe", ApplicationStatus.Applied, "Backend Intern");
        var shopify = await h.AddAppAsync("Shopify", ApplicationStatus.Applied);
        h.Gmail.Messages.Add(Mail("m1", "Priya <priya@stripe.com>", "Next steps for your Backend Intern application", "SECRET-BODY-TEXT we'd love to schedule a phone screen."));
        h.Classifier.Script = i => Says(ApplicationStatus.Interview, 0.87, "Stripe wants to schedule a phone screen.", new DateTime(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc));

        var result = await h.SyncAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.MessagesFound);
        Assert.Equal(1, result.Suggestions);
        var input = Assert.Single(h.Classifier.Inputs);
        Assert.Equal("Stripe", input.Company);
        Assert.Equal("Backend Intern", input.Role);
        Assert.Equal(ApplicationStatus.Applied, input.CurrentStatus);
        Assert.Contains("SECRET-BODY-TEXT", input.Body);

        var s = Assert.Single(await h.SuggestionsAsync());
        Assert.Equal(stripe, s.ApplicationId);
        Assert.NotEqual(shopify, s.ApplicationId);
        Assert.Equal("m1", s.GmailMessageId);
        Assert.Equal(ApplicationStatus.Interview, s.SuggestedStatus);
        Assert.Equal(0.87, s.Confidence, 3);
        Assert.Equal("Stripe wants to schedule a phone screen.", s.Summary);
        Assert.Equal(new DateTime(2026, 9, 22, 18, 0, 0), s.InterviewAt);
        Assert.Equal("Next steps for your Backend Intern application", s.EmailSubject);
        Assert.Equal("Priya <priya@stripe.com>", s.EmailFrom);
        Assert.Equal(SuggestionState.Pending, s.Status);

        // Nothing in the row (or anywhere else in the table) carries the body.
        using var scope = h.Factory.Services.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var raw = await db.Database.SqlQueryRaw<string>("SELECT \"Summary\" || \"EmailSubject\" || \"EmailFrom\" || \"GmailMessageId\" AS \"Value\" FROM \"StatusSuggestions\"").SingleAsync();
        Assert.DoesNotContain("SECRET-BODY-TEXT", raw);

        // First sync uses the 30-day look-back, and the connection is now stamped.
        Assert.Contains("newer_than:30d", h.Gmail.Queries[0]);
        Assert.Contains("from:stripe.com", h.Gmail.Queries[0]);
        Assert.Contains("\"Shopify\"", h.Gmail.Queries[0]);
        Assert.NotNull((await h.ConnectionAsync()).LastSyncedAt);
    }

    [Fact]
    public async Task Company_name_plus_keyword_matches_when_the_sender_domain_does_not()
    {
        using var h = await new Host().ConnectedAsync();
        var acme = await h.AddAppAsync("Acme Robotics", ApplicationStatus.Applied);
        h.Gmail.Messages.Add(Mail("k1", "Talent Team <talent@greenhouse-mail.io>", "Your application to Acme Robotics", "Unfortunately we will not be moving forward."));
        h.Gmail.Messages.Add(Mail("k2", "Newsletter <news@somewhere.io>", "Weekly digest", "Nothing about any company here."));
        h.Classifier.Script = i => Says(ApplicationStatus.Rejected, 0.95, "Acme declined.");

        var result = await h.SyncAsync();

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Classified);
        var s = Assert.Single(await h.SuggestionsAsync());
        Assert.Equal(acme, s.ApplicationId);
        Assert.Equal(ApplicationStatus.Rejected, s.SuggestedStatus);
    }

    [Fact]
    public async Task Link_and_note_domains_count_as_the_companys_own()
    {
        using var h = await new Host().ConnectedAsync();
        await h.AddAppAsync("Datadog", ApplicationStatus.Applied, link: "https://careers.datadoghq.com/detail/sre", note: "Recruiter is Sam <sam@dd-recruiting.io>");
        h.Gmail.Messages.Add(Mail("d1", "Sam <sam@dd-recruiting.io>", "Quick chat?"));
        h.Gmail.Messages.Add(Mail("d2", "no-reply@mail.datadoghq.com", "Interview confirmation"));
        h.Classifier.Script = i => Says(ApplicationStatus.Interview);

        var result = await h.SyncAsync();

        Assert.Equal(2, result.Matched);
        Assert.Contains("from:datadoghq.com", h.Gmail.Queries[0]);
        Assert.Contains("from:dd-recruiting.io", h.Gmail.Queries[0]);
    }

    [Fact]
    public async Task Duplicate_message_ids_are_never_classified_twice()
    {
        using var h = await new Host().ConnectedAsync();
        await h.AddAppAsync("Stripe", ApplicationStatus.Applied);
        h.Gmail.Messages.Add(Mail("dup", "hr@stripe.com", "Offer letter"));
        h.Classifier.Script = i => Says(ApplicationStatus.Offer);

        var first  = await h.SyncAsync();
        var second = await h.SyncAsync();   // the fake inbox still returns the same message id

        Assert.Equal(1, first.Suggestions);
        Assert.Equal(0, second.Suggestions);
        Assert.Equal(0, second.Classified);
        Assert.Single(h.Classifier.Inputs);
        Assert.Single(await h.SuggestionsAsync());
        Assert.Contains("after:", h.Gmail.Queries[1]);
        Assert.DoesNotContain("newer_than", h.Gmail.Queries[1]);
    }

    [Fact]
    public async Task Suggestions_equal_to_the_current_status_are_dropped()
    {
        using var h = await new Host().ConnectedAsync();
        await h.AddAppAsync("Stripe", ApplicationStatus.Interview);
        h.Gmail.Messages.Add(Mail("same", "hr@stripe.com", "Interview reminder"));
        h.Classifier.Script = i => Says(ApplicationStatus.Interview);

        var result = await h.SyncAsync();

        Assert.Equal(1, result.Classified);
        Assert.Equal(0, result.Suggestions);
        Assert.Empty(await h.SuggestionsAsync());
    }

    [Fact]
    public async Task Non_matching_null_status_and_unparsable_answers_store_nothing()
    {
        using var h = await new Host().ConnectedAsync();
        await h.AddAppAsync("Stripe", ApplicationStatus.Applied);
        h.Gmail.Messages.Add(Mail("a", "hr@stripe.com", "A"));
        h.Gmail.Messages.Add(Mail("b", "hr@stripe.com", "B"));
        h.Gmail.Messages.Add(Mail("c", "hr@stripe.com", "C"));
        h.Classifier.Script = i => i.Subject switch
        {
            "A" => new StatusClassification(false, ApplicationStatus.Offer, 0.9, "wrong thread", null),
            "B" => Says(null),
            _   => null
        };

        var result = await h.SyncAsync();

        Assert.Equal(3, result.Classified);
        Assert.Empty(await h.SuggestionsAsync());
    }

    [Fact]
    public async Task Saved_and_rejected_applications_are_not_searched()
    {
        using var h = await new Host().ConnectedAsync();
        await h.AddAppAsync("Stripe", ApplicationStatus.Saved);
        await h.AddAppAsync("Notion", ApplicationStatus.Rejected);
        h.Gmail.Messages.Add(Mail("x", "hr@stripe.com", "Interview"));

        var result = await h.SyncAsync();

        Assert.True(result.Success);
        Assert.Empty(h.Gmail.Queries);           // nothing to look for → Gmail is not even called
        Assert.Empty(h.Classifier.Inputs);
    }

    [Fact]
    public async Task Ai_calls_draw_from_the_users_shared_rate_limit_bucket()
    {
        using var h = await new Host(("RateLimiting:AI:PermitLimit", "2")).ConnectedAsync();
        await h.AddAppAsync("Stripe", ApplicationStatus.Applied);
        for (var i = 0; i < 3; i++) h.Gmail.Messages.Add(Mail("r" + i, "hr@stripe.com", "Mail " + i));
        h.Classifier.Script = i => Says(ApplicationStatus.Offer);

        var result = await h.SyncAsync();

        Assert.Equal(2, result.Classified);
        Assert.Equal(1, result.RateLimited);
        Assert.Contains("AI limit reached", result.Message);
        Assert.Null((await h.ConnectionAsync()).LastSyncedAt);   // window stays open so the skipped mail is searched again

        // The HTTP "ai" endpoints see the same empty bucket.
        var res = await h.Client.PostAsJsonAsync("/Analyzer/Analyze", new { jobDescription = "" });
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
    }

    [Fact]
    public async Task Expiring_access_token_is_refreshed_and_stored_encrypted()
    {
        using var h = await new Host().ConnectedAsync();
        await h.AddAppAsync("Stripe", ApplicationStatus.Applied);
        await h.SetConnectionAsync(c => c.TokenExpiresAt = DateTime.UtcNow.AddMinutes(2));

        var result = await h.SyncAsync();

        Assert.True(result.Success);
        Assert.Equal(new[] { FakeGoogleOAuthClient.RefreshToken }, h.OAuth.RefreshedTokens);
        Assert.Contains(FakeGoogleOAuthClient.AccessToken + ".refreshed", h.Gmail.AccessTokensSeen);
        var c = await h.ConnectionAsync();
        Assert.True(c.TokenExpiresAt > DateTime.UtcNow.AddMinutes(30));
        Assert.DoesNotContain("ya29", c.AccessToken);
        using var scope = h.Factory.Services.CreateScope();
        Assert.Equal(FakeGoogleOAuthClient.AccessToken + ".refreshed", scope.ServiceProvider.GetRequiredService<GmailTokenProtector>().Unprotect(c.AccessToken));
    }

    [Fact]
    public async Task Sync_now_endpoint_reports_the_counts_as_a_toast()
    {
        using var h = await new Host().ConnectedAsync();
        await h.AddAppAsync("Stripe", ApplicationStatus.Applied);
        h.Gmail.Messages.Add(Mail("t1", "hr@stripe.com", "Offer"));
        h.Gmail.Messages.Add(Mail("t2", "hr@stripe.com", "Offer details"));
        h.Classifier.Script = i => i.Subject == "Offer" ? Says(ApplicationStatus.Offer) : Says(null);

        var token = await Http.GetAntiforgeryTokenAsync(h.Client, "/Profile");
        var res = await h.Client.PostAsync("/Integrations/Gmail/Sync", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/Profile", res.Headers.Location!.ToString());
        var html = await (await h.Client.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.Contains("Checked 2 emails, 1 new suggestion.", html);
        Assert.Contains("Last synced", html);
    }

    [Fact]
    public async Task Hosted_job_syncs_every_connected_user_and_survives_one_failing_account()
    {
        using var h = await new Host().ConnectedAsync();
        await h.AddAppAsync("Stripe", ApplicationStatus.Applied);
        h.Gmail.Messages.Add(Mail("h1", "hr@stripe.com", "Offer"));
        h.Classifier.Script = i => Says(ApplicationStatus.Offer);

        // A second connection whose tokens can't be unprotected any more (a dead row).
        using (var scope = h.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.GmailConnections.Add(new GmailConnection { UserId = "ghost-user", GmailAddress = "ghost@gmail.test", AccessToken = "junk", RefreshToken = "junk", TokenExpiresAt = DateTime.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }

        var job = h.Factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<GmailSyncHostedService>().Single();
        await job.RunOnceAsync(CancellationToken.None);

        Assert.Single(await h.SuggestionsAsync());
    }
}
