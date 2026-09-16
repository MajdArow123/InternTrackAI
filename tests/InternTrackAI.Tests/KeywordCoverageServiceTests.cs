using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// The keyword-coverage rules, tested through the pure statics: extraction from a posting, literal
/// word-boundary matching against a resume, prominence ordering, and the four "nothing to show" reasons.
/// No DI, no host, no network.
/// </summary>
public class KeywordCoverageServiceTests
{
    /// <summary>A resume long enough to clear the minimum-length floor, with known wording.</summary>
    private const string Resume =
        "MAJD AROW Toronto, ON TECHNICAL SKILLS Languages: C#, Java, Python, JavaScript, PHP, SQL. " +
        "Web and Frameworks: ASP.NET Core, React, HTML, CSS, EF Core, REST APIs. " +
        "Databases: PostgreSQL, MySQL. Tools: Git, GitHub, VS Code, Agile. " +
        "EXPERIENCE Built services and wrote automated tests for a checkout platform.";

    /// <summary>A posting with enough distinct terms to clear <see cref="KeywordCoverageService.MinTerms"/>.</summary>
    private static string Posting(params string[] extraLines) => string.Join("\n", new[]
    {
        "Backend Engineering Intern",
        "",
        "Requirements:",
        "* Experience with Docker and Kubernetes",
        "* Familiarity with Terraform, Grafana, and Prometheus",
        "* Knowledge of Jenkins, Django, and Angular",
        "* Understanding of RabbitMQ and Cassandra",
        "* Exposure to Kafka",
    }.Concat(extraLines));

    private static KeywordCoverage Build(string posting, string resume = Resume, string? company = null,
                                         string? role = null, string? location = null)
        => KeywordCoverageService.Build(posting, resume, company, role, location);

    private static List<string> Terms(string posting, string? company = null, string? role = null, string? location = null)
        => KeywordCoverageService.Extract(posting, company, role, location).Select(t => t.Term).ToList();

    private static List<string> MissingOf(KeywordCoverage c) => c.Missing.Select(m => m.Term).ToList();

    // ── The substring decision ───────────────────────────

    [Fact]
    public void A_posting_term_is_not_covered_by_a_longer_resume_word_containing_it()
    {
        // The whole premise of the feature: a scanner looking for the literal string "SQL" does not find it
        // inside "PostgreSQL". Reporting it as covered would answer a different question than the one asked.
        Assert.False(KeywordCoverageService.Present("SQL", "Databases: PostgreSQL, MySQL."));
        Assert.True(KeywordCoverageService.Present("SQL", "Databases: PostgreSQL, SQL, MySQL."));
    }

    [Fact]
    public void Java_is_not_covered_by_JavaScript()
    {
        // The false positive that makes substring matching unacceptable: the user would be told they cover
        // Java and would act on it by not fixing anything.
        Assert.False(KeywordCoverageService.Present("Java", "Languages: JavaScript, TypeScript"));
        Assert.True(KeywordCoverageService.Present("Java", "Languages: Java, JavaScript"));
    }

    [Theory]
    [InlineData("PostgreSQL", "Databases: PostgreSQL")]     // the containing word itself still matches
    [InlineData("MySQL", "Databases: PostgreSQL, MySQL")]
    [InlineData("React", "Frameworks: React.js and Redux")] // trailing punctuation is a boundary
    public void A_term_matches_itself_on_a_word_boundary(string term, string resume)
        => Assert.True(KeywordCoverageService.Present(term, resume));

    [Fact]
    public void A_term_starting_with_punctuation_relaxes_its_leading_boundary()
    {
        // ".NET" has to be covered by "ASP.NET Core" — that is how nearly every .NET resume writes it.
        // Without the exception nobody would ever cover it.
        Assert.True(KeywordCoverageService.Present(".NET", "Web and Frameworks: ASP.NET Core, React"));
        // The trailing boundary still applies, which is what keeps the SQL/PostgreSQL case strict.
        Assert.False(KeywordCoverageService.Present(".NET", "Frameworks: ASP.NETCore"));
    }

    // ── Normalisation and aliases ────────────────────────

    [Theory]
    [InlineData("docker", "Deployed with Docker")]
    [InlineData("DOCKER", "Deployed with Docker")]
    [InlineData("Docker", "deployed with docker")]
    public void Matching_is_case_insensitive(string term, string resume)
        => Assert.True(KeywordCoverageService.Present(term, resume));

    [Theory]
    [InlineData("API", "Built REST APIs for checkout")]          // singular term, plural resume
    [InlineData("APIs", "Built a REST API for checkout")]        // plural term, singular resume
    [InlineData("pipelines", "Owned the release pipeline")]
    [InlineData("microservice", "Split the monolith into microservices")]
    public void Simple_plurals_are_folded(string term, string resume)
        => Assert.True(KeywordCoverageService.Present(term, resume));

    [Theory]
    [InlineData("developer", "the developer's workflow")]
    [InlineData("developer", "the developer’s workflow")]        // curly apostrophe, as pasted from the web
    public void Possessives_are_folded(string term, string resume)
        => Assert.True(KeywordCoverageService.Present(term, resume));

    [Fact]
    public void Stemming_stops_at_plurals_because_a_scanner_does_too()
    {
        // "testing" and "tested" are different strings to an ATS, so they must stay different here.
        Assert.False(KeywordCoverageService.Present("testing", "I tested the release pipeline"));
        Assert.False(KeywordCoverageService.Present("tested", "Responsible for testing"));
    }

    [Theory]
    [InlineData("JavaScript", "Languages: JS, Python")]
    [InlineData("JS", "Languages: JavaScript, Python")]
    [InlineData("Kubernetes", "Ran workloads on K8s")]
    [InlineData("K8s", "Ran workloads on Kubernetes")]
    [InlineData(".NET", "Built services in dotnet")]
    [InlineData("dotnet", "Built services in .NET 8")]
    [InlineData("CI/CD", "Owned the CICD pipeline")]
    [InlineData("CICD", "Owned the CI/CD pipeline")]
    [InlineData("PostgreSQL", "Databases: Postgres, Redis")]
    [InlineData("Postgres", "Databases: PostgreSQL, Redis")]
    public void Known_equivalences_count_as_covered(string term, string resume)
        => Assert.True(KeywordCoverageService.Present(term, resume));

    [Fact]
    public void The_alias_map_is_the_one_shared_with_the_skill_gap_card()
        => Assert.Same(SkillAliases.Map, SkillGapService.Aliases);

    // ── Phrase matching ──────────────────────────────────

    [Fact]
    public void A_multi_word_term_matches_as_a_phrase_not_as_loose_words()
    {
        Assert.True(KeywordCoverageService.Present("machine learning", "Applied machine learning to ranking"));
        // Both words present, never adjacent — an ATS scanning for the phrase finds nothing.
        Assert.False(KeywordCoverageService.Present("machine learning", "Learning to operate the machine"));
    }

    [Fact]
    public void Phrase_matching_ignores_the_gap_between_words_being_different_whitespace()
        => Assert.True(KeywordCoverageService.Present("Spring Boot", "Built APIs with Spring Boot and Kafka"));

    // ── The near-miss hint ───────────────────────────────

    [Fact]
    public void A_missing_term_hidden_inside_a_longer_resume_word_reports_it()
    {
        // Without this the user whose resume says PostgreSQL reads "SQL: missing" as a bug rather than
        // as the point of the feature.
        Assert.Equal("PostgreSQL", KeywordCoverageService.NearMiss("SQL", "Databases: PostgreSQL only"));
        Assert.Equal("JavaScript", KeywordCoverageService.NearMiss("Java", "Languages: JavaScript"));
    }

    [Fact]
    public void There_is_no_near_miss_when_the_term_is_actually_present_or_absent()
    {
        Assert.Null(KeywordCoverageService.NearMiss("SQL", "Databases: SQL and PostgreSQL"));  // covered
        Assert.Null(KeywordCoverageService.NearMiss("Rust", "Languages: Python, Go"));          // simply absent
    }

    [Fact]
    public void The_near_miss_hint_is_attached_to_the_listed_term()
    {
        var coverage = Build(Posting("* Familiarity with SQL and NoSQL databases"),
                             resume: Resume.Replace(", SQL.", "."));   // PostgreSQL stays, bare SQL goes

        var sql = coverage.Missing.FirstOrDefault(m => m.Term == "SQL");
        Assert.NotNull(sql);
        Assert.Equal("PostgreSQL", sql!.NearMiss);
    }

    // ── Extraction ───────────────────────────────────────

    [Fact]
    public void Punctuated_technology_names_survive_as_one_term()
    {
        var terms = Terms("Requirements:\n* Experience with ASP.NET Core, Node.js, CI/CD, Vue.js, and C++\n" +
                          "* Knowledge of Docker, Kubernetes, Terraform, Grafana, and Prometheus");

        Assert.Contains("ASP.NET", terms);
        Assert.Contains("Node.js", terms);
        Assert.Contains("CI/CD", terms);
        Assert.Contains("Vue.js", terms);
        // The acronym pass must not have re-split them.
        Assert.DoesNotContain("ASP", terms);
        Assert.DoesNotContain("CI", terms);
        Assert.DoesNotContain("CD", terms);
    }

    [Fact]
    public void A_posting_written_without_bullet_glyphs_still_yields_its_terms()
    {
        // A real posting formatted this way once had every requirement line swallowed as a heading.
        // "Experience with Docker or Kubernetes" is a requirement, not a section header.
        var terms = Terms(string.Join("\n",
            "Required Skills:",
            "",
            "Proficiency in Java, Python, or C#",
            "Understanding of REST APIs and HTTP fundamentals",
            "Familiarity with Git and version control workflows",
            "Experience with Docker or Kubernetes",
            "Knowledge of cloud platforms (IBM Cloud, AWS, or Azure)",
            "Familiarity with SQL or NoSQL databases",
            "Experience with Agile/Scrum methodology"));

        foreach (var expected in new[] { "Docker", "Kubernetes", "AWS", "Azure", "NoSQL", "HTTP", "Java", "Python" })
            Assert.Contains(expected, terms);
    }

    [Fact]
    public void A_posting_with_no_line_structure_still_yields_its_technologies()
    {
        // The bookmarklet/HTML-strip path can deliver a posting as one blob. Extraction degrades to raw
        // frequency ordering rather than breaking.
        var flat = "About the role We are hiring. Experience with ASP.NET Core, Node.js, and Django. " +
                   "Familiarity with React, Angular, or Vue.js. Understanding of Docker and Kubernetes. " +
                   "Exposure to AWS, Azure, and Terraform. Knowledge of Grafana and Prometheus.";

        var terms = Terms(flat);

        foreach (var expected in new[] { "ASP.NET", "Node.js", "Django", "Angular", "Docker", "Kubernetes", "Terraform" })
            Assert.Contains(expected, terms);
    }

    [Fact]
    public void Section_headings_are_not_terms()
    {
        var terms = Terms(Posting("Technical Skills", "* Experience with Redis", "Nice to Have", "* Knowledge of Elasticsearch"));

        Assert.DoesNotContain("Technical Skills", terms);
        Assert.DoesNotContain("Nice to Have", terms);
        Assert.Contains("Redis", terms);
        Assert.Contains("Elasticsearch", terms);
    }

    [Fact]
    public void The_employers_own_name_role_and_city_are_not_terms()
    {
        var terms = Terms(Posting("* Join Shopify in Toronto as a Backend Engineering Intern"),
                          company: "Shopify", role: "Backend Engineering Intern", location: "Toronto, ON");

        Assert.DoesNotContain("Shopify", terms);
        Assert.DoesNotContain("Toronto", terms);
    }

    [Fact]
    public void Month_names_are_not_terms()
        => Assert.DoesNotContain("August", Terms(Posting("Duration: 4 months (May 2026 - August 2026)")));

    [Fact]
    public void A_term_wholly_inside_a_more_frequent_longer_term_is_dropped()
    {
        var terms = Terms(Posting("* Familiarity with Azure DevOps", "* Experience with Azure DevOps pipelines"));

        Assert.Contains("Azure DevOps", terms);
        Assert.DoesNotContain("Azure", terms);
    }

    // ── Noise on branded, non-technical postings ─────────
    //
    // A requirements list is the easy case. A rotational-program posting is the common one for a new grad:
    // heavy branding, a pay line, every role in the program named, and a degree requirement — all of which
    // look like keywords and none of which a resume can usefully be edited to contain.

    [Fact]
    public void The_employers_name_is_theirs_however_the_posting_joins_it()
    {
        // Stored as "Manulife"; written as "Manulife/John Hancock". Both halves are branding.
        var terms = Terms(Posting("* Experience with Azure and APIM at Manulife/John Hancock"), company: "Manulife");

        Assert.DoesNotContain("Manulife/John", terms);
        Assert.DoesNotContain("Hancock", terms);
        Assert.DoesNotContain("John", terms);
        Assert.Contains("APIM", terms);
    }

    [Theory]
    [InlineData("Manulife / John Hancock")]
    [InlineData("Manulife|John Hancock")]
    [InlineData("John Hancock/Manulife")]
    public void A_joined_brand_is_claimed_whichever_way_it_is_written(string written)
    {
        var terms = Terms(Posting($"* Reporting into {written} technology"), company: "Manulife");

        Assert.DoesNotContain("Hancock", terms);
        Assert.DoesNotContain("John", terms);
    }

    [Fact]
    public void Plain_adjacency_to_the_employer_is_not_branding()
    {
        // The regression this rule has to avoid: "Microsoft Azure" must keep Azure as a real keyword,
        // even though it sits directly against the employer's name.
        var terms = Terms(Posting("* Build on Microsoft Azure and Microsoft Entra"), company: "Microsoft");

        Assert.Contains("Azure", terms);
        Assert.Contains("Entra", terms);
    }

    [Theory]
    [InlineData("Salary: CAD 55,000 - 65,000 annually")]
    [InlineData("Compensation: $32-38/hour")]
    [InlineData("Base pay range 70000 USD per year")]
    public void A_pay_line_contributes_nothing(string payLine)
    {
        var terms = Terms(Posting("* " + payLine));

        Assert.DoesNotContain("CAD", terms);
        Assert.DoesNotContain("USD", terms);
        Assert.DoesNotContain("Salary", terms);
        Assert.DoesNotContain("Base", terms);
    }

    [Fact]
    public void A_currency_code_is_never_a_keyword_even_outside_a_pay_line()
        => Assert.DoesNotContain("CAD", Terms(Posting("* Reporting in CAD across the team")));

    [Fact]
    public void Two_letter_fragments_are_dropped_but_real_short_acronyms_survive()
    {
        var terms = Terms(Posting("* Familiarity with AD, AI, ML, and QA", "* Exposure to AKS, ACS, and APIM"));

        Assert.DoesNotContain("AD", terms);      // a fragment, not a technology
        Assert.Contains("AI", terms);
        Assert.Contains("ML", terms);
        Assert.Contains("QA", terms);
        Assert.Contains("APIM", terms);          // three letters and up are kept on sight
    }

    [Theory]
    [InlineData("Business Analyst")]
    [InlineData("Data Engineer")]
    [InlineData("Site Reliability Engineer")]
    [InlineData("Product Manager")]
    public void Job_titles_are_not_keywords(string title)
        => Assert.DoesNotContain(title, Terms(Posting($"* Rotations include {title} placements")));

    [Theory]
    [InlineData("Computer Science")]
    [InlineData("Computer Engineering")]
    [InlineData("Electrical Engineering")]
    [InlineData("Applied Mathematics")]
    public void Degree_fields_are_not_keywords(string field)
        => Assert.DoesNotContain(field, Terms(Posting($"* Enrolled in a {field} program")));

    [Fact]
    public void A_technology_that_merely_ends_in_a_role_word_is_kept()
    {
        // The rule keys on the last word, so check it hasn't swallowed real multi-word product names.
        var terms = Terms(Posting("* Experience with Entity Framework Core, Spring Boot, and Visual Studio",
                                  "* Knowledge of Google Cloud Platform and Azure DevOps"));

        foreach (var kept in new[] { "Entity Framework Core", "Spring Boot", "Visual Studio", "Google Cloud Platform", "Azure DevOps" })
            Assert.Contains(kept, terms);
    }

    [Fact]
    public void A_branded_program_posting_keeps_only_the_technologies()
    {
        // Reconstructed from a real posting's reported output (Manulife GRO): the shapes that produced
        // junk chips, alongside the terms that were genuinely worth showing.
        var posting = string.Join("\n",
            "Global Rotational Opportunities (GRO) Program — Manulife/John Hancock",
            "Toronto, ON | Hybrid",
            "Salary: CAD 55,000 - 70,000 annually",
            "",
            "About the Program",
            "The GRO Program places new graduates across John Hancock and Manulife technology teams.",
            "Rotations include Business Analyst, Data Engineer, and Software Developer placements.",
            "",
            "Qualifications:",
            "Enrolled in Computer Science, Computer Engineering, or a related discipline",
            "Familiarity with AD and identity tooling",
            "Experience with Azure, AKS/ACS, and APIM",
            "Understanding of DevOps and design patterns",
            "Knowledge of Terraform and Kubernetes");

        var terms = Terms(posting, company: "Manulife", role: "GRO Program", location: "Toronto, ON");

        foreach (var junk in new[] { "Manulife/John", "Hancock", "John", "CAD", "AD",
                                     "Business Analyst", "Data Engineer", "Software Developer",
                                     "Computer Engineering", "Computer Science" })
            Assert.DoesNotContain(junk, terms);

        foreach (var real in new[] { "AKS/ACS", "APIM", "Azure", "DevOps", "design patterns", "Terraform", "Kubernetes" })
            Assert.Contains(real, terms);
    }

    // ── Ordering, counting and the cap ───────────────────

    [Fact]
    public void Terms_in_a_requirements_section_outrank_equally_frequent_ones_elsewhere()
    {
        var coverage = Build(string.Join("\n", new[]
        {
            "About the role",
            "We use Mesos here.",
        }.Concat(Posting("* Some exposure to Nomad").Split('\n'))));

        var missing = MissingOf(coverage);
        Assert.Contains("Mesos", missing);
        // Both appear once; the one under Requirements gets the boost.
        Assert.True(missing.IndexOf("Nomad") < missing.IndexOf("Mesos"));
    }

    [Fact]
    public void A_more_frequent_term_outranks_a_rarer_one()
    {
        var coverage = Build(Posting(
            "* Experience with Rust",
            "* Rust is used across our services",
            "* Our Rust codebase is growing",
            "* Some exposure to Haskell"));

        var missing = MissingOf(coverage);
        Assert.True(missing.IndexOf("Rust") < missing.IndexOf("Haskell"));
    }

    [Fact]
    public void Equally_ranked_terms_are_ordered_alphabetically_so_the_list_is_stable()
    {
        var coverage = Build(Posting("* Knowledge of Zephyr, Ansible, and Nomad"));
        var ranked = MissingOf(coverage).Where(t => t is "Ansible" or "Nomad" or "Zephyr").ToList();

        Assert.Equal(new[] { "Ansible", "Nomad", "Zephyr" }, ranked);
    }

    [Fact]
    public void The_missing_list_is_capped_but_the_denominator_counts_every_term()
    {
        var lines = Enumerable.Range(0, 30).Select(i => $"* Experience with Zetatool{i:D2}");
        var coverage = Build("Requirements:\n" + string.Join("\n", lines));

        Assert.True(coverage.Available);
        Assert.Equal(KeywordCoverageService.MaxMissing, coverage.Missing.Count);
        Assert.True(coverage.Total >= 30);
        Assert.Equal(0, coverage.Covered);
    }

    [Fact]
    public void Covered_plus_missing_accounts_for_every_extracted_term()
    {
        var coverage = Build(Posting("* Experience with Docker, Python, React, and PostgreSQL"));

        Assert.True(coverage.Available);
        Assert.True(coverage.Covered > 0);                 // the resume really does have Python/React/PostgreSQL
        Assert.True(coverage.Covered < coverage.Total);
    }

    [Fact]
    public void A_term_the_resume_covers_is_not_listed_as_missing()
    {
        var missing = MissingOf(Build(Posting("* Experience with Python, React, and PostgreSQL")));

        Assert.DoesNotContain("Python", missing);
        Assert.DoesNotContain("React", missing);
        Assert.DoesNotContain("PostgreSQL", missing);
    }

    [Fact]
    public void Each_listed_term_carries_the_posting_line_it_came_from()
    {
        var coverage = Build(Posting("* Familiarity with Pulumi for infrastructure"));
        var pulumi = coverage.Missing.First(m => m.Term == "Pulumi");

        Assert.Contains("Pulumi", pulumi.Context);
        Assert.Contains("infrastructure", pulumi.Context);
    }

    // ── The four unavailable reasons ─────────────────────

    [Fact]
    public void No_description_says_so()
    {
        foreach (var empty in new string?[] { null, "", "   " })
        {
            var coverage = KeywordCoverageService.Build(empty, Resume);
            Assert.False(coverage.Available);
            Assert.Equal(KeywordCoverageService.NoDescription, coverage.Reason);
        }
    }

    [Fact]
    public void No_resume_says_so()
    {
        var coverage = KeywordCoverageService.Build(Posting(), null);

        Assert.False(coverage.Available);
        Assert.Equal(KeywordCoverageService.NoResume, coverage.Reason);
    }

    [Fact]
    public void A_resume_that_parsed_to_almost_nothing_says_so_rather_than_failing_every_term()
    {
        // The scanned-image case. Declaring all 31 terms missing would be actively misleading.
        var coverage = KeywordCoverageService.Build(Posting(), "Resume");

        Assert.False(coverage.Available);
        Assert.Equal(KeywordCoverageService.UnreadableResume, coverage.Reason);
        Assert.Empty(coverage.Missing);
    }

    [Fact]
    public void A_posting_too_thin_to_extract_from_says_so_rather_than_showing_a_stub_list()
    {
        var coverage = KeywordCoverageService.Build("Backend intern wanted. Apply within.", Resume);

        Assert.False(coverage.Available);
        Assert.Equal(KeywordCoverageService.TooShort, coverage.Reason);
    }

    [Fact]
    public void The_minimum_term_count_is_the_boundary_between_too_short_and_usable()
    {
        var terms = Enumerable.Range(0, KeywordCoverageService.MinTerms).Select(i => $"* Experience with Zetatool{i:D2}");
        var justEnough = KeywordCoverageService.Build("Requirements:\n" + string.Join("\n", terms), Resume);

        Assert.True(justEnough.Available);
        Assert.True(justEnough.Total >= KeywordCoverageService.MinTerms);
    }

    // ── Input safety ─────────────────────────────────────

    [Fact]
    public void An_enormous_posting_is_truncated_rather_than_scanned_whole()
    {
        var huge = Posting() + "\n" + string.Join("\n",
            Enumerable.Range(0, 5000).Select(i => $"* Experience with Zetatool{i:D5}"));

        var coverage = Build(huge);

        Assert.True(coverage.Available);
        // Everything past the budget is invisible, so the very last tool never appears.
        Assert.DoesNotContain("Zetatool04999", MissingOf(coverage));
    }

    [Fact]
    public void Regex_metacharacters_in_a_posting_are_treated_as_text()
    {
        // Terms come from untrusted pasted text and are used to build patterns, so they must be escaped.
        var coverage = Build(Posting(@"* Experience with C++ and F#", @"* Knowledge of A(B)C and [Dd]"));

        Assert.True(coverage.Available);   // did not throw
        Assert.Contains("C++", MissingOf(coverage));
    }
}
