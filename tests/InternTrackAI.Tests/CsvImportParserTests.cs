using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

public class CsvImportParserTests
{
    private const string Header = "Company,Role,Location,Work Mode,Status,Deadline,Date Applied,Salary,Job Link";

    private static CsvImportParser.Result Parse(string csv, IEnumerable<(string, string)>? existing = null)
        => CsvImportParser.Parse(new StringReader(csv), "user-1", existing);

    [Fact]
    public void Parses_valid_rows_with_every_column()
    {
        var csv = Header + "\n" +
                  "Stripe,Backend Intern,\"San Francisco, CA\",Hybrid,Interview,2026-10-01,2026-08-28,$50/hr,https://stripe.com/jobs/2\n" +
                  "Notion,Frontend Intern,Remote,Remote,Offer,2026-09-30,2026-08-10,$48/hr,https://notion.so/careers/4\n";

        var r = Parse(csv);

        Assert.Equal(2, r.Applications.Count);
        Assert.Equal(0, r.Skipped);
        Assert.Equal(0, r.Duplicates);

        var stripe = r.Applications[0];
        Assert.Equal("user-1", stripe.UserId);
        Assert.Equal("Stripe", stripe.CompanyName);
        Assert.Equal("Backend Intern", stripe.RoleTitle);
        Assert.Equal("San Francisco, CA", stripe.Location);            // quoted field containing a comma
        Assert.Equal(WorkMode.Hybrid, stripe.WorkMode);
        Assert.Equal(ApplicationStatus.Interview, stripe.Status);
        Assert.Equal(new DateTime(2026, 10, 1), stripe.Deadline);
        Assert.Equal(new DateTime(2026, 8, 28), stripe.DateApplied);
        Assert.Equal("$50/hr", stripe.Salary);
        Assert.Equal("https://stripe.com/jobs/2", stripe.JobLink);

        Assert.Equal(ApplicationStatus.Offer, r.Applications[1].Status);
    }

    [Fact]
    public void Skips_rows_missing_company_or_role_but_keeps_the_rest()
    {
        var csv = Header + "\n" +
                  ",Ghost Role,Remote\n" +          // no company
                  "Ghost Co,,Remote\n" +            // no role
                  "   ,   \n" +                     // whitespace only
                  "Real Co,Real Role\n";            // short row: only the two required columns

        var r = Parse(csv);

        Assert.Single(r.Applications);
        Assert.Equal(3, r.Skipped);
        var app = r.Applications[0];
        Assert.Equal("Real Co", app.CompanyName);
        Assert.Null(app.Location);
        Assert.Null(app.Deadline);
        Assert.Equal(WorkMode.Remote, app.WorkMode);            // default when column absent
        Assert.Equal(ApplicationStatus.Saved, app.Status);      // default when column absent
    }

    [Fact]
    public void Header_only_and_blank_lines_produce_nothing()
    {
        var r = Parse(Header + "\n\n   \n");
        Assert.Empty(r.Applications);
        Assert.Equal(0, r.Skipped);
        Assert.Equal(0, r.Duplicates);
    }

    [Fact]
    public void Bad_dates_become_null_and_do_not_fail_the_row()
    {
        var csv = Header + "\n" +
                  "Acme,Intern,,Remote,Applied,not-a-date,31/31/2026,,\n" +
                  "Beta,Intern,,Remote,Applied,2026-13-45,,,\n";

        var r = Parse(csv);

        Assert.Equal(2, r.Applications.Count);
        Assert.All(r.Applications, a => { Assert.Null(a.Deadline); Assert.Null(a.DateApplied); });
        Assert.Equal(ApplicationStatus.Applied, r.Applications[0].Status);
    }

    [Fact]
    public void Unknown_status_and_work_mode_fall_back_to_defaults_case_insensitively()
    {
        var csv = Header + "\n" +
                  "Acme,Intern,,onsite,interview,,,,\n" +
                  "Beta,Intern,,Underwater,Ghosted,,,,\n";

        var r = Parse(csv);

        Assert.Equal(WorkMode.OnSite, r.Applications[0].WorkMode);
        Assert.Equal(ApplicationStatus.Interview, r.Applications[0].Status);
        Assert.Equal(WorkMode.Remote, r.Applications[1].WorkMode);
        Assert.Equal(ApplicationStatus.Saved, r.Applications[1].Status);
    }

    [Fact]
    public void Detects_duplicates_within_the_file()
    {
        var csv = Header + "\n" +
                  "Stripe,Backend Intern\n" +
                  "stripe,backend intern\n" +       // same pair, different case
                  " Stripe , Backend Intern \n" +   // same pair, padded
                  "Stripe,Frontend Intern\n";       // different role → kept

        var r = Parse(csv);

        Assert.Equal(2, r.Applications.Count);
        Assert.Equal(2, r.Duplicates);
        Assert.Equal(0, r.Skipped);
    }

    [Fact]
    public void Detects_duplicates_against_existing_applications()
    {
        var csv = Header + "\n" +
                  "Stripe,Backend Intern\n" +
                  "Figma,Design Intern\n";

        var r = Parse(csv, existing: new[] { ("STRIPE", "Backend Intern") });

        Assert.Single(r.Applications);
        Assert.Equal("Figma", r.Applications[0].CompanyName);
        Assert.Equal(1, r.Duplicates);
    }

    [Fact]
    public void Round_trips_the_export_quoting_rules()
    {
        // A field with an embedded quote is exported as "" inside quotes.
        var fields = CsvImportParser.ParseLine("\"Acme \"\"Labs\"\", Inc\",\"Role, Senior\",plain,,end");
        Assert.Equal(new[] { "Acme \"Labs\", Inc", "Role, Senior", "plain", "", "end" }, fields);
    }
}
