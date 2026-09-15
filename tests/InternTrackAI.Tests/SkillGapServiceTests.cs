using System.Text.Json;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>Pure-rule tests for <see cref="SkillGapService.Build"/>: counting, normalisation, aliases, exclusions, role buckets, takeaway, threshold.</summary>
public class SkillGapServiceTests
{
    private static int _nextId = 1;

    private static JobApplication Raw(string? missingJson, ApplicationStatus status = ApplicationStatus.Applied, string role = "Software Engineering Intern", string company = "Co") =>
        new() { Id = _nextId++, UserId = "u", CompanyName = company, RoleTitle = role, Status = status, MissingSkillsJson = missingJson };

    private static JobApplication App(params string[] missing) => Raw(JsonSerializer.Serialize(missing));

    private static JobApplication RoleApp(string role, params string[] missing) => Raw(JsonSerializer.Serialize(missing), role: role);

    private static SkillGapViewModel Build(IEnumerable<JobApplication> apps, IEnumerable<string>? roles = null, int topN = SkillGapService.DefaultTopN) =>
        SkillGapService.Build(apps, roles ?? Array.Empty<string>(), topN: topN);

    private static SkillGapEntry Skill(SkillGapBucket bucket, string name) => Assert.Single(bucket.Skills, s => s.Skill == name);

    // ── Counting ─────────────────────────────────────────

    [Fact]
    public void Counts_distinct_applications_across_many_and_a_repeat_within_one_application_counts_once()
    {
        var a = App("Docker", "Go", "Docker");
        var b = App("Docker");
        var c = App("Go");
        var vm = Build(new[] { a, b, c });

        var docker = Skill(vm.All, "Docker");
        Assert.Equal(2, docker.Count);
        Assert.Equal(new[] { a.Id, b.Id }.OrderByDescending(i => i), docker.ApplicationIds.OrderByDescending(i => i));
        Assert.Equal(2, Skill(vm.All, "Go").Count);
        Assert.Equal(3, vm.AnalyzedApplications);
        Assert.Equal(67, docker.Percent);   // 2 of 3
    }

    [Fact]
    public void Percent_uses_every_analyzed_application_including_ones_with_nothing_missing()
    {
        var vm = Build(new[] { App("Docker"), App(), App(), App() });   // three "[]" rows still count as analyzed
        Assert.Equal(4, vm.AnalyzedApplications);
        Assert.Equal(25, Skill(vm.All, "Docker").Percent);
    }

    // ── Normalisation ────────────────────────────────────

    [Fact]
    public void Compares_case_insensitively_after_trimming_and_collapsing_whitespace()
    {
        var vm = Build(new[] { App("  Machine   Learning "), App("machine learning"), App("MACHINE LEARNING") });
        var entry = Assert.Single(vm.All.Skills);
        Assert.Equal(3, entry.Count);
    }

    [Fact]
    public void Displays_the_most_common_casing()
    {
        var apps = Enumerable.Range(0, 2).Select(_ => App("docker"))
            .Concat(Enumerable.Range(0, 8).Select(_ => App("Docker")))
            .Concat(new[] { App("DOCKER") });
        var entry = Assert.Single(Build(apps).All.Skills);
        Assert.Equal("Docker", entry.Skill);
        Assert.Equal(11, entry.Count);
    }

    [Fact]
    public void Casing_tie_goes_to_the_spelling_seen_first()
    {
        // Build orders by newest first (DateApplied, then Id), so the highest id is "seen" first.
        var older = App("graphQL");
        var newer = App("GraphQL");
        Assert.Equal("GraphQL", Assert.Single(Build(new[] { older, newer }).All.Skills).Skill);
    }

    // ── Aliases ──────────────────────────────────────────

    [Theory]
    [InlineData("Postgres", "PostgreSQL", "PostgreSQL")]
    [InlineData("Node", "NodeJS", "Node.js")]
    [InlineData("CICD", "CI CD", "CI/CD")]
    [InlineData("K8s", "kubernetes", "Kubernetes")]
    [InlineData("JS", "javascript", "JavaScript")]
    public void Alias_map_merges_obvious_variants_under_the_canonical_name(string first, string second, string display)
    {
        var entry = Assert.Single(Build(new[] { App(first), App(second), App(first) }).All.Skills);
        Assert.Equal(display, entry.Skill);
        Assert.Equal(3, entry.Count);
    }

    [Fact]
    public void Aliases_within_one_application_count_once()
    {
        var entry = Assert.Single(Build(new[] { App("Postgres", "PostgreSQL") }).All.Skills);
        Assert.Equal(1, entry.Count);
    }

    [Fact]
    public void Does_not_over_merge_react_and_react_native_or_java_and_javascript()
    {
        var vm = Build(new[] { App("React", "Java"), App("React Native", "JavaScript"), App("ReactJS") });
        Assert.Equal(2, Skill(vm.All, "React").Count);
        Assert.Equal(1, Skill(vm.All, "React Native").Count);
        Assert.Equal(1, Skill(vm.All, "Java").Count);
        Assert.Equal(1, Skill(vm.All, "JavaScript").Count);
    }

    [Fact]
    public void Every_alias_value_is_its_own_canonical_spelling()
    {
        foreach (var canonical in SkillGapService.Aliases.Values.Distinct())
            Assert.Equal(canonical, SkillGapService.Aliases[canonical.ToLowerInvariant()]);
    }

    // ── Exclusions / malformed data ──────────────────────

    [Fact]
    public void Saved_is_excluded_and_rejected_is_included()
    {
        var vm = Build(new[]
        {
            Raw("[\"Docker\"]", ApplicationStatus.Saved), Raw("[\"Docker\"]", ApplicationStatus.Saved),
            Raw("[\"Rust\"]", ApplicationStatus.Rejected),
            Raw("[\"Go\"]", ApplicationStatus.Interview),
            Raw("[\"Go\"]", ApplicationStatus.Offer),
        });

        Assert.Equal(3, vm.AnalyzedApplications);
        Assert.DoesNotContain(vm.All.Skills, s => s.Skill == "Docker");
        Assert.Equal(1, Skill(vm.All, "Rust").Count);
        Assert.Equal(2, Skill(vm.All, "Go").Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[\"Docker\",")]
    [InlineData("{\"skills\":[\"Docker\"]}")]
    [InlineData("\"Docker\"")]
    public void Null_blank_malformed_or_non_array_json_is_skipped_without_throwing(string? json)
    {
        var vm = Build(new[] { Raw(json), App("Go"), App("Go"), App("Go") });
        Assert.Equal(3, vm.AnalyzedApplications);
        Assert.Equal("Go", Assert.Single(vm.All.Skills).Skill);
    }

    [Fact]
    public void Empty_array_is_analyzed_with_nothing_missing_and_odd_entries_are_ignored()
    {
        var vm = Build(new[] { Raw("[]"), Raw("[]"), Raw("[null, 3, \"\", \"  \", {\"a\":1}]") });
        Assert.Equal(3, vm.AnalyzedApplications);
        Assert.Empty(vm.All.Skills);
        Assert.Equal(SkillGapService.NothingMissing, vm.All.Takeaway);
    }

    // ── Role buckets ─────────────────────────────────────

    [Fact]
    public void Application_matching_two_tags_appears_in_both_and_one_matching_none_lands_in_other()
    {
        var both  = RoleApp("Backend Software Engineering Intern", "Docker");
        var swe   = RoleApp("Software Engineering Intern (Ruby/Rails)", "Ruby");
        var other = RoleApp("iOS Developer Intern", "Swift");
        var vm = Build(new[] { both, swe, other }, new[] { "Software Engineering", "Backend Intern" });

        var sweBucket     = Assert.Single(vm.Roles, r => r.Label == "Software Engineering");
        var backendBucket = Assert.Single(vm.Roles, r => r.Label == "Backend Intern");
        Assert.Equal(2, sweBucket.Analyzed);
        Assert.Contains(sweBucket.Skills, s => s.ApplicationIds.Contains(both.Id));
        Assert.Equal(1, backendBucket.Analyzed);
        Assert.Contains(backendBucket.Skills, s => s.ApplicationIds.Contains(both.Id));

        Assert.NotNull(vm.Other);
        Assert.Equal(1, vm.Other!.Analyzed);
        Assert.Equal(other.Id, Assert.Single(Assert.Single(vm.Other.Skills).ApplicationIds));
        Assert.True(vm.ShowRoleFilter);
    }

    [Fact]
    public void Role_matching_is_whole_word_and_case_insensitive()
    {
        Assert.True(SkillGapService.MatchesRole("Site Reliability ENGINEERING Intern", "engineering intern"));
        Assert.True(SkillGapService.MatchesRole("Intern, Software Engineering", "Software Engineering Intern"));  // any order
        Assert.False(SkillGapService.MatchesRole("Software Engineer Intern", "Software Engineering Intern"));    // "Engineer" is not "Engineering"
        Assert.False(SkillGapService.MatchesRole("Javascript Developer", "Java Developer"));
        Assert.False(SkillGapService.MatchesRole("Data Intern", "   "));
    }

    [Fact]
    public void Other_is_null_when_every_application_matches_a_role_or_there_are_no_roles()
    {
        var apps = new[] { RoleApp("Data Science Intern", "R"), RoleApp("Data Science Intern", "SQL"), RoleApp("Data Science Intern", "SQL") };
        Assert.Null(Build(apps, new[] { "Data Science" }).Other);
        Assert.Null(Build(apps).Other);
    }

    [Fact]
    public void Role_filter_is_hidden_unless_two_roles_have_data_and_empty_roles_say_so()
    {
        var apps = new[] { RoleApp("Data Science Intern", "R"), RoleApp("Data Science Intern", "SQL"), RoleApp("Web Intern", "SQL") };
        var vm = Build(apps, new[] { "Data Science", "Product Manager" });

        Assert.False(vm.ShowRoleFilter);
        var empty = Assert.Single(vm.Roles, r => r.Label == "Product Manager");
        Assert.Equal(0, empty.Analyzed);
        Assert.Equal(SkillGapService.NoRoleData, empty.Takeaway);
    }

    [Fact]
    public void Target_role_filter_selects_that_bucket()
    {
        var apps = new[] { RoleApp("Data Science Intern", "R"), RoleApp("Web Intern", "CSS"), RoleApp("Web Intern", "CSS") };
        var vm = SkillGapService.Build(apps, new[] { "Data Science", "Web" }, targetRoleFilter: "web");
        Assert.Equal("Web", vm.Selected.Label);
        Assert.Equal("CSS", Assert.Single(vm.Selected.Skills).Skill);
        Assert.Equal(3, vm.All.Analyzed);
    }

    // ── Ordering / topN ──────────────────────────────────

    [Fact]
    public void Top_n_limits_the_list_and_orders_by_count_descending()
    {
        var apps = new List<JobApplication>();
        for (var i = 0; i < 12; i++)
            apps.Add(App(Enumerable.Range(0, 12 - i).Select(k => "Skill" + (char)('A' + k)).ToArray()));   // SkillA in 12, SkillL in 1

        var vm = Build(apps, topN: 10);
        Assert.Equal(10, vm.All.Skills.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(k => "Skill" + (char)('A' + k)), vm.All.Skills.Select(s => s.Skill));
        Assert.True(vm.All.Skills.Zip(vm.All.Skills.Skip(1)).All(p => p.First.Count >= p.Second.Count));
        Assert.Equal(3, Build(apps, topN: 3).All.Skills.Count);
    }

    // ── Takeaway ─────────────────────────────────────────

    [Fact]
    public void Takeaway_names_a_clear_top_skill_with_count_and_percent()
    {
        var apps = new List<JobApplication>();
        for (var i = 0; i < 31; i++)
        {
            var skills = new List<string>();
            if (i < 12) skills.Add("Docker");
            if (i < 5) skills.Add("Kubernetes");
            if (i < 3) skills.Add("GraphQL");
            apps.Add(App(skills.ToArray()));
        }
        Assert.Equal("Docker came up in 12 of your 31 analyzed applications (39%).", Build(apps).All.Takeaway);
    }

    [Fact]
    public void Takeaway_groups_the_top_three_when_within_two_counts()
    {
        var apps = new List<JobApplication>();
        for (var i = 0; i < 10; i++)
        {
            var skills = new List<string>();
            if (i < 6) skills.Add("Docker");
            if (i < 5) skills.Add("Kubernetes");
            if (i < 4) skills.Add("GraphQL");
            if (i < 1) skills.Add("Rust");
            apps.Add(App(skills.ToArray()));
        }
        Assert.Equal("Docker, Kubernetes, and GraphQL come up most often.", Build(apps).All.Takeaway);
    }

    [Fact]
    public void Takeaway_says_no_pattern_when_every_skill_appears_once()
    {
        var vm = Build(new[] { App("Docker"), App("Go"), App("Rust", "Swift") });
        Assert.Equal(SkillGapService.NoPattern, vm.All.Takeaway);
    }

    // ── Visibility threshold ─────────────────────────────

    [Fact]
    public void Two_analyzed_applications_hide_the_card_and_three_show_it()
    {
        var two = Build(new[] { App("Docker"), App("Docker"), Raw(null), Raw("[\"Docker\"]", ApplicationStatus.Saved) });
        Assert.Equal(2, two.AnalyzedApplications);
        Assert.True(two.Hide);

        var three = Build(new[] { App("Docker"), App("Docker"), App("Go") });
        Assert.False(three.Hide);
        Assert.True(three.LowSample);
        Assert.Equal(2, Skill(three.All, "Docker").Count);

        var five = Build(Enumerable.Range(0, 5).Select(_ => App("Go")));
        Assert.False(five.LowSample);
    }

    // ── Demo seed ────────────────────────────────────────

    [Fact]
    public void Demo_seed_has_a_clear_top_skill_a_mid_tier_a_tail_and_data_for_both_target_roles()
    {
        var apps = DemoSeeder.BuildApplications("u", new DateTime(2026, 9, 13));
        for (var i = 0; i < apps.Count; i++) apps[i].Id = i + 1;

        var vm = SkillGapService.Build(apps, DemoSeeder.TargetRoles);

        Assert.Equal(11, vm.AnalyzedApplications);
        Assert.Equal("Docker came up in 6 of your 11 analyzed applications (55%).", vm.All.Takeaway);
        Assert.Equal(new[] { "Docker", "Go", "C++" }, vm.All.Skills.Take(3).Select(s => s.Skill));
        Assert.Contains(vm.All.Skills, s => s.Count == 1);
        Assert.True(vm.ShowRoleFilter);
        Assert.All(vm.Roles, r => Assert.True(r.Analyzed >= 2, r.Label));
        Assert.NotNull(vm.Other);
    }
}
