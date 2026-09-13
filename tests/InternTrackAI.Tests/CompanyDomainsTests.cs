using InternTrackAI.Services.Gmail;

namespace InternTrackAI.Tests;

/// <summary>The company → sender-domain normaliser used by the Gmail sync.</summary>
public class CompanyDomainsTests
{
    [Theory]
    [InlineData("Stripe", "stripe.com")]
    [InlineData("Stripe, Inc.", "stripe.com")]
    [InlineData("Shopify Ltd", "shopify.com")]
    [InlineData("Acme Corp", "acme.com")]
    [InlineData("Google LLC", "google.com")]
    [InlineData("Jane Street Capital", "janestreetcapital.com")]
    [InlineData("Coca-Cola Company", "coca-cola.com")]
    [InlineData("AT&T", "atandt.com")]
    [InlineData("  datadog  ", "datadog.com")]
    [InlineData("Corp", "corp.com")]           // a lone legal word is still a name
    public void Company_name_becomes_a_dot_com_domain(string name, string expected) =>
        Assert.Equal(expected, CompanyDomains.FromCompanyName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Unusable_names_give_null(string? name) => Assert.Null(CompanyDomains.FromCompanyName(name));

    [Fact]
    public void Job_link_contributes_its_host_and_registrable_domain()
    {
        var domains = CompanyDomains.FromUrl("https://careers.stripe.com/jobs/listing/123?src=x").ToList();
        Assert.Contains("careers.stripe.com", domains);
        Assert.Contains("stripe.com", domains);
        Assert.Empty(CompanyDomains.FromUrl("not a url"));
        Assert.Empty(CompanyDomains.FromUrl("mailto:hr@acme.com"));
    }

    [Fact]
    public void Notes_contribute_the_domains_of_any_email_addresses_in_them()
    {
        var domains = CompanyDomains.FromText("Recruiter: Priya <priya.k@mail.acme.io>. Also cc hiring@acme.io").ToList();
        Assert.Contains("mail.acme.io", domains);
        Assert.Contains("acme.io", domains);
    }

    [Fact]
    public void For_merges_all_sources_and_drops_generic_mailboxes()
    {
        var set = CompanyDomains.For("Acme Corp", "https://jobs.lever.co/acme/123", new[] { "sent from my gmail.com: me@gmail.com; recruiter: a@acme.io" });
        Assert.Contains("acme.com", set);
        Assert.Contains("acme.io", set);
        Assert.DoesNotContain("gmail.com", set);
        Assert.DoesNotContain("lever.co", set);
    }

    [Theory]
    [InlineData("stripe.com", true)]
    [InlineData("mail.stripe.com", true)]
    [InlineData("notstripe.com", false)]
    [InlineData("stripe.com.evil.io", false)]
    [InlineData(null, false)]
    public void Sender_matches_a_candidate_or_one_of_its_subdomains(string? sender, bool expected) =>
        Assert.Equal(expected, CompanyDomains.Matches(sender, new[] { "stripe.com" }));

    [Theory]
    [InlineData("Priya K <priya@stripe.com>", "stripe.com")]
    [InlineData("recruiting@Mail.Stripe.com", "mail.stripe.com")]
    [InlineData("\"Stripe Recruiting\" <no-reply@stripe.com>", "stripe.com")]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    public void Sender_domain_is_read_from_the_From_header(string from, string? expected) =>
        Assert.Equal(expected, GmailApiClient.SenderDomain(from));
}
