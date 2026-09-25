using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Account deletion as a user meets it: the path to it from Profile, what the page promises, and that the
/// promise matches what <see cref="UserDataPurger"/> actually clears. The list on the page went stale once
/// already (it named "interview prep sessions" for months after that table was dropped, and left out four
/// tables the purge was clearing), so it is pinned per table here rather than trusted.
/// </summary>
public class DeletePersonalDataTests : IClassFixture<TestAppFactory>
{
    private const string DeletePage = "/Identity/Account/Manage/DeletePersonalData";
    private const string Password   = "Integration-Pass-1!";

    private readonly TestAppFactory _factory;

    public DeletePersonalDataTests(TestAppFactory factory) => _factory = factory;

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// Every app table that holds a user's rows: a DbSet declared on the app's context whose entity carries a
    /// string <c>UserId</c>. Identity's own tables (users, claims, logins, tokens) are inherited, not declared,
    /// and are removed by <c>UserManager.DeleteAsync</c> — the page's "Your login" line.
    /// </summary>
    private static IReadOnlyDictionary<string, Type> UserTables() =>
        typeof(ApplicationDbContext).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => (Name: p.Name, Entity: p.PropertyType.GetGenericArguments()[0]))
            .Where(t => t.Entity.GetProperty("UserId")?.PropertyType == typeof(string))
            .ToDictionary(t => t.Name, t => t.Entity);

    private static async Task<int> CountFor(ApplicationDbContext db, Type entity, string userId)
    {
        var method = typeof(DeletePersonalDataTests).GetMethod(nameof(CountForTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(entity);
        return await (Task<int>)method.Invoke(null, new object[] { db, userId })!;
    }

    private static Task<int> CountForTyped<T>(ApplicationDbContext db, string userId) where T : class =>
        db.Set<T>().CountAsync(e => EF.Property<string>(e, "UserId") == userId);

    private async Task<(HttpClient Client, string UserId)> RegisterAsync()
    {
        var client = Client();
        var email = await Http.RegisterAsync(client, password: Password);
        using var scope = _factory.Services.CreateScope();
        var id = (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
        return (client, id);
    }

    private static async Task<HttpResponseMessage> DeleteAccountAsync(HttpClient client)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, DeletePage);
        return await client.PostAsync(DeletePage, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));
    }

    [Fact]
    public async Task The_page_lists_exactly_the_tables_the_purge_clears()
    {
        var (client, _) = await RegisterAsync();
        var html = await (await client.GetAsync(DeletePage)).Content.ReadAsStringAsync();

        var listed = Regex.Matches(html, "data-purges=\"([^\"]+)\"")
            .SelectMany(m => m.Groups[1].Value.Split(','))
            .Select(s => s.Trim())
            .ToHashSet();

        var tables = UserTables().Keys.ToHashSet();
        Assert.True(tables.Count >= 9, "the reflection found too few user tables to be looking at the right context");

        var unlisted = tables.Except(listed).ToList();
        var phantom  = listed.Except(tables).ToList();
        Assert.True(unlisted.Count == 0, "the delete page does not mention: " + string.Join(", ", unlisted));
        Assert.True(phantom.Count == 0, "the delete page names tables that do not exist: " + string.Join(", ", phantom));
        Assert.DoesNotContain("interview prep sessions", html);
    }

    [Fact]
    public async Task Deleting_the_account_leaves_no_row_in_any_user_table()
    {
        var (client, userId) = await RegisterAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication { UserId = userId, CompanyName = "Shopify", RoleTitle = "Backend Intern", Status = ApplicationStatus.Applied };
            db.JobApplications.Add(app);
            var resume = new ResumeVersion { UserId = userId, VersionNumber = 1, OriginalFileName = "cv.pdf", StoredPath = $"resumes/{userId}/missing.pdf", IsActive = true };
            db.ResumeVersions.Add(resume);
            await db.SaveChangesAsync();

            db.ApplicationNotes.Add(new ApplicationNote { UserId = userId, JobApplicationId = app.Id, Text = "Phone screen booked." });
            db.GeneratedCoverLetters.Add(new GeneratedCoverLetter { UserId = userId, JobApplicationId = app.Id, Content = "Dear team", IsActive = true, VersionNumber = 1 });
            db.PracticeQuestions.Add(new PracticeQuestion
            {
                UserId = userId, ApplicationId = app.Id, Prompt = "Explain idempotency?", PromptHash = QuestionHash.Of("Explain idempotency?"),
                Topic = "idempotency", Difficulty = PracticeDifficulty.Medium, Category = QuestionCategory.Technical, CreatedAt = DateTime.UtcNow
            });
            db.StatusSuggestions.Add(new StatusSuggestion
            {
                UserId = userId, ApplicationId = app.Id, GmailMessageId = "m-1", SuggestedStatus = ApplicationStatus.Interview,
                Confidence = 0.9, Summary = "Interview invite", EmailSubject = "Next steps", EmailFrom = "recruiting@shopify.test"
            });
            db.ParsedResumes.Add(new ParsedResume { UserId = userId, ResumeVersionId = resume.Id, RawJson = "{}", CreatedAt = DateTime.UtcNow });
            // Plain strings, not ciphertext: they don't unprotect, so the real Google client is never called.
            db.GmailConnections.Add(new GmailConnection { UserId = userId, GmailAddress = "me@gmail.test", AccessToken = "x", RefreshToken = "y", TokenExpiresAt = DateTime.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();

            // A new user table must be seeded here too, or this test proves nothing about it.
            foreach (var (name, entity) in UserTables())
                Assert.True(await CountFor(db, entity, userId) > 0, $"{name} was not seeded, so its deletion is untested");
        }

        var res = await DeleteAccountAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var (name, entity) in UserTables())
                Assert.True(await CountFor(db, entity, userId) == 0, $"{name} still holds rows after the account was deleted");
            Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByIdAsync(userId));
        }
    }

    [Fact]
    public async Task Profile_links_straight_to_deletion_and_to_the_privacy_policy()
    {
        var (client, _) = await RegisterAsync();
        var html = await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync();

        var card = Regex.Match(html, "id=\"yourDataCard\">(.*?)</div>", RegexOptions.Singleline);
        Assert.True(card.Success, "the Your data card is missing from Profile");
        Assert.Contains($"href=\"{DeletePage}\"", card.Groups[1].Value);
        Assert.Contains("href=\"/Home/Privacy\"", card.Groups[1].Value);
    }

    [Fact]
    public async Task Privacy_and_terms_are_public_and_point_at_each_other_and_at_deletion()
    {
        var anonymous = Client();

        var privacy = await anonymous.GetAsync("/Home/Privacy");
        Assert.Equal(HttpStatusCode.OK, privacy.StatusCode);
        var privacyHtml = WebUtility.HtmlDecode(await privacy.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Use this page to detail", privacyHtml);
        Assert.Contains($"href=\"{DeletePage}\"", privacyHtml);
        Assert.Contains("href=\"/Home/Terms\"", privacyHtml);
        Assert.Contains("gmail.readonly", privacyHtml);
        Assert.Contains("Message bodies are never stored", privacyHtml);
        Assert.Contains("IP address to Google", privacyHtml);

        var terms = await anonymous.GetAsync("/Home/Terms");
        Assert.Equal(HttpStatusCode.OK, terms.StatusCode);
        var termsHtml = WebUtility.HtmlDecode(await terms.Content.ReadAsStringAsync());
        Assert.Contains("shared and public", termsHtml);
        Assert.Contains("href=\"/Home/Privacy\"", termsHtml);
    }

    [Theory]
    [InlineData("/")]                                  // landing footer
    [InlineData("/Home/Privacy")]                      // app layout footer
    [InlineData("/Identity/Account/Login")]            // auth layout footnote
    [InlineData("/Identity/Account/Register")]
    public async Task Every_layout_links_the_privacy_policy_and_terms(string url)
    {
        var html = await (await Client().GetAsync(url)).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/Home/Privacy\"", html);
        Assert.Contains("href=\"/Home/Terms\"", html);
    }

    [Fact]
    public async Task The_signed_in_app_footer_links_the_privacy_policy()
    {
        var (client, _) = await RegisterAsync();
        var res = await client.GetAsync("/Home/Dashboard");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        var footer = Regex.Match(html, "<footer[^>]*class=\"site-footer\">(.*?)</footer>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("href=\"/Home/Privacy\"", footer);
        Assert.Contains("href=\"/Home/Terms\"", footer);
    }
}

/// <summary>Account deletion with Gmail connected: the grant is revoked with Google, and a Google failure never stops the deletion.</summary>
public class DeletePersonalDataGmailTests
{
    private const string Password = "Integration-Pass-1!";

    private static async Task ConnectAsync(HttpClient client)
    {
        var res = await client.GetAsync("/Integrations/Gmail/Connect");
        var state = HttpUtility.ParseQueryString(new Uri(res.Headers.Location!.ToString()).Query)["state"]!;
        var cb = await client.GetAsync($"/Integrations/Gmail/Callback?code=4/code&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/Manage/DeletePersonalData");
        return await client.PostAsync("/Identity/Account/Manage/DeletePersonalData", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_the_account_revokes_the_Gmail_grant_and_deletes_even_if_Google_fails(bool googleFails)
    {
        var (parent, factory, oauth, _) = GmailTestHost.Boot();
        using var _p = parent; using var _f = factory;

        var client = GmailTestHost.Client(factory);
        var email = await Http.RegisterAsync(client, password: Password);
        await ConnectAsync(client);
        if (googleFails) oauth.RevokeFailure = new HttpRequestException("Google unreachable");

        var res = await DeleteAsync(client);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(new[] { FakeGoogleOAuthClient.RefreshToken }, oauth.RevokedTokens);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().GmailConnections.CountAsync());
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email));
    }

    [Fact]
    public async Task Deleting_an_account_without_Gmail_never_calls_Google()
    {
        var (parent, factory, oauth, _) = GmailTestHost.Boot();
        using var _p = parent; using var _f = factory;

        var client = GmailTestHost.Client(factory);
        await Http.RegisterAsync(client, password: Password);

        Assert.Equal(HttpStatusCode.Redirect, (await DeleteAsync(client)).StatusCode);
        Assert.Empty(oauth.RevokedTokens);
    }
}
