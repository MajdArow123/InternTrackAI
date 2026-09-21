using System.Net;
using System.Text.Json;
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
/// End-to-end behaviour of the field-awareness fields on the profile: they round-trip through
/// <c>SaveInfo</c>, junk in the enum slots is coerced rather than rejected, the role-suggestion island
/// carries every category, and — the guarantee that matters most — a user who has never set any of them
/// sees exactly the app they saw before the columns existed.
/// </summary>
public class FieldAwarenessTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public FieldAwarenessTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<string> UserIdOf(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
    }

    private async Task<UserProfile?> ProfileOf(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId);
    }

    /// <summary>
    /// Deletes the profile row registration creates (Register.cshtml.cs), to reproduce the oldest
    /// account shape: a user with no <see cref="UserProfile"/> at all.
    /// </summary>
    private async Task RemoveProfileRow(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.UserProfiles.Where(p => p.UserId == userId).ToListAsync();
        db.UserProfiles.RemoveRange(rows);
        await db.SaveChangesAsync();
    }

    /// <summary>Posts SaveInfo with the required fields plus whatever field-awareness values are given.</summary>
    private static async Task<JsonElement> SaveInfo(HttpClient client, IDictionary<string, string> values)
    {
        var form = new Dictionary<string, string>
        {
            ["fullName"] = "Test Person",
            ["__RequestVerificationToken"] = await Http.GetAntiforgeryTokenAsync(client, "/Profile"),
        };
        foreach (var (k, v) in values) form[k] = v;

        var req = new HttpRequestMessage(HttpMethod.Post, "/Profile/SaveInfo") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task The_field_values_round_trip_through_SaveInfo_and_render_back_on_the_page()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        var body = await SaveInfo(client, new Dictionary<string, string>
        {
            ["field"]           = "  Registered Nursing  ",
            ["fieldCategory"]   = "Healthcare",
            ["seniority"]       = "Student",
            ["yearsExperience"] = "2",
            ["location"]        = " Toronto, ON ",
        });

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("Healthcare", body.GetProperty("fieldCategory").GetString());

        var profile = await ProfileOf(userId);
        Assert.Equal("Registered Nursing", profile!.Field);
        Assert.Equal(FieldCategory.Healthcare, profile.FieldCategory);
        Assert.Equal(SeniorityLevel.Student, profile.Seniority);
        Assert.Equal(2, profile.YearsExperience);
        Assert.Equal("Toronto, ON", profile.Location);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync());
        Assert.Contains("Registered Nursing", html);
        Assert.Contains("Toronto, ON", html);
    }

    [Fact]
    public async Task An_unrecognised_category_is_coerced_to_Other_and_an_unrecognised_level_is_dropped()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        var body = await SaveInfo(client, new Dictionary<string, string>
        {
            ["field"]         = "Beekeeping",
            ["fieldCategory"] = "Apiculture",
            ["seniority"]     = "Grandmaster",
        });

        // A save, not a 400: the rest of the form must not be lost over one bad select value.
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("Other", body.GetProperty("fieldCategory").GetString());

        var profile = await ProfileOf(userId);
        Assert.Equal("Beekeeping", profile!.Field);
        Assert.Equal(FieldCategory.Other, profile.FieldCategory);
        Assert.Null(profile.Seniority);
    }

    [Fact]
    public async Task Years_of_experience_are_clamped_rather_than_stored_as_given()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        await SaveInfo(client, new Dictionary<string, string> { ["yearsExperience"] = "9999" });

        Assert.Equal(ProfileFields.MaxYearsExperience, (await ProfileOf(userId))!.YearsExperience);
    }

    [Fact]
    public async Task A_client_that_omits_the_new_inputs_saves_the_rest_without_failing()
    {
        // A cached page from before this feature posts no field/category/seniority at all.
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        var body = await SaveInfo(client, new Dictionary<string, string> { ["country"] = "Canada" });

        Assert.True(body.GetProperty("success").GetBoolean());
        var profile = await ProfileOf(userId);
        Assert.Equal("Canada", profile!.Country);
        Assert.Null(profile.Field);
        Assert.Null(profile.FieldCategory);
    }

    [Fact]
    public async Task Clearing_the_field_clears_it_rather_than_keeping_the_old_value()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        await SaveInfo(client, new Dictionary<string, string> { ["field"] = "Accounting", ["fieldCategory"] = "Finance" });
        await SaveInfo(client, new Dictionary<string, string> { ["field"] = "", ["fieldCategory"] = "" });

        var profile = await ProfileOf(userId);
        Assert.Null(profile!.Field);
        Assert.Null(profile.FieldCategory);
    }

    [Fact]
    public async Task The_profile_page_ships_role_suggestions_for_every_category_and_no_hardcoded_technology_list()
    {
        var client = NewClient();
        await Http.RegisterAsync(client);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync());

        Assert.Contains("roleSuggestionsData", html);
        // The island is what the combobox reads; a nursing user must be able to find nursing roles in it.
        Assert.Contains("Nursing Student Placement", html);
        Assert.Contains("Electrician Apprentice", html);
        Assert.Contains("Software Engineering Intern", html);
        // The old list lived in profile.js; the view must not have grown its own copy.
        Assert.DoesNotContain("ROLE_PRESETS", html);
    }

    /// <summary>
    /// The no-regression guarantee for the users who already had accounts: with every new column null,
    /// each AI endpoint must still reach its own logic and fail only on the absent API key — never on a
    /// missing profile row, a null enum or an empty context block.
    /// </summary>
    [Fact]
    public async Task With_no_field_set_every_AI_endpoint_still_reaches_its_normal_no_api_key_path()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));
        await RemoveProfileRow(userId);
        Assert.Null(await ProfileOf(userId));   // no profile row at all: the oldest possible account shape

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");

        // Analyzer: JSON body, no antiforgery token needed.
        var analyze = await client.PostAsync("/Analyzer/Analyze",
            new StringContent("{\"jobDescription\":\"We are hiring a ward nurse.\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, analyze.StatusCode);
        Assert.Contains("API key", await analyze.Content.ReadAsStringAsync());

        // Salary insight: JSON body plus the header token.
        var salaryReq = new HttpRequestMessage(HttpMethod.Post, "/SalaryInsight/Estimate")
        {
            Content = new StringContent("{\"role\":\"Ward Nurse\",\"company\":\"A Hospital\"}", System.Text.Encoding.UTF8, "application/json")
        };
        salaryReq.Headers.Add("RequestVerificationToken", token);
        var salary = await client.SendAsync(salaryReq);
        Assert.Equal(HttpStatusCode.OK, salary.StatusCode);
        Assert.Contains("API key", await salary.Content.ReadAsStringAsync());

        // Resume match: no resume on this account, so it answers hasResume:false — the point is that it
        // answers at all rather than throwing while building an empty context.
        var matchReq = new HttpRequestMessage(HttpMethod.Post, "/Profile/AutoMatch")
        {
            Content = new StringContent("{\"jobDescription\":\"Ward nursing, 12-hour shifts.\"}", System.Text.Encoding.UTF8, "application/json")
        };
        var match = await client.SendAsync(matchReq);
        Assert.Equal(HttpStatusCode.OK, match.StatusCode);
        Assert.Contains("hasResume", await match.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_profile_row_with_every_new_column_null_renders_the_page()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        // The shape an account created before this feature has: a filled-in profile, every new column null.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.UserProfiles.SingleAsync(p => p.UserId == userId);
            profile.FullName = "Existing User";
            await db.SaveChangesAsync();
        }

        Assert.Null((await ProfileOf(userId))!.Field);

        var res = await client.GetAsync("/Profile");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Existing User", WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// The context block reaching a prompt service, asserted where the repo already has a stubbable AI
    /// service: the analyzer. Anything else would be asserting on a string the model never sees.
    /// </summary>
    [Fact]
    public async Task The_context_block_built_for_a_user_carries_their_field_into_the_prompt()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        await SaveInfo(client, new Dictionary<string, string>
        {
            ["field"]         = "Registered Nursing",
            ["fieldCategory"] = "Healthcare",
            ["seniority"]     = "Student",
        });

        using var scope = _factory.Services.CreateScope();
        var block = await scope.ServiceProvider.GetRequiredService<IUserContextBuilder>().BuildAsync(userId);

        Assert.Equal(string.Join("\n",
            "USER PROFILE CONTEXT",
            "Field: Registered Nursing (Healthcare)",
            "Seniority: Student"), block);
    }

    [Fact]
    public async Task A_user_with_no_profile_row_gets_an_empty_block_not_a_half_built_one()
    {
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));
        await RemoveProfileRow(userId);

        using var scope = _factory.Services.CreateScope();
        var block = await scope.ServiceProvider.GetRequiredService<IUserContextBuilder>().BuildAsync(userId);

        // Nothing to say about the user at all, so nothing is said — Prefix then adds no stray blank line.
        Assert.Equal("", block);
        Assert.Equal("", UserContextBuilder.Prefix(block));
    }

    [Fact]
    public async Task A_profile_row_with_no_field_yields_the_unspecified_instruction()
    {
        // The common case for existing accounts: the row exists (registration creates it) but Field is
        // null, so the model is told to infer the field from the posting rather than being told nothing.
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        using var scope = _factory.Services.CreateScope();
        var block = await scope.ServiceProvider.GetRequiredService<IUserContextBuilder>().BuildAsync(userId);

        Assert.Equal("USER PROFILE CONTEXT\n" + UserContextBuilder.UnspecifiedFieldLine, block);
    }
}
