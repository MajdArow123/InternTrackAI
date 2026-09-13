using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>Pure-rule tests for <see cref="ResumeAnalyticsService.Build"/>: buckets, exclusions, rate math, low-confidence flag, takeaway.</summary>
public class ResumeAnalyticsServiceTests
{
    private static ResumeVersion Resume(int id, int version, bool active = false, string? label = null, string file = "resume.pdf") =>
        new() { Id = id, UserId = "u", VersionNumber = version, IsActive = active, Label = label, OriginalFileName = file };

    private static JobApplication App(ApplicationStatus status, int? resumeId, int? score = null) =>
        new() { UserId = "u", CompanyName = "Co", RoleTitle = "Intern", Status = status, ResumeVersionId = resumeId, MatchScore = score };

    private static IEnumerable<JobApplication> Many(ApplicationStatus status, int? resumeId, int n) =>
        Enumerable.Range(0, n).Select(_ => App(status, resumeId));

    // ── Buckets ──────────────────────────────────────────

    [Fact]
    public void One_row_per_resume_plus_a_no_resume_bucket_for_unlinked_applications()
    {
        var resumes = new[] { Resume(1, 1), Resume(2, 2, active: true) };
        var apps = new[] { App(ApplicationStatus.Applied, 1), App(ApplicationStatus.Applied, 2), App(ApplicationStatus.Applied, null), App(ApplicationStatus.Applied, 99) };

        var rows = ResumeAnalyticsService.Build(resumes, apps).Rows;

        Assert.Equal(3, rows.Count);
        Assert.Single(rows, r => r.ResumeVersionId == 1);
        Assert.Single(rows, r => r.ResumeVersionId == 2 && r.IsActive);
        var none = Assert.Single(rows, r => r.IsNoResumeBucket);
        Assert.Equal(ResumeAnalyticsService.NoResumeLabel, none.Label);
        Assert.Equal(2, none.Sent);   // null link + link to a deleted version
    }

    [Fact]
    public void No_resume_bucket_is_omitted_when_every_sent_application_is_linked()
    {
        var rows = ResumeAnalyticsService.Build(new[] { Resume(1, 1) }, new[] { App(ApplicationStatus.Applied, 1), App(ApplicationStatus.Saved, null) }).Rows;
        Assert.DoesNotContain(rows, r => r.IsNoResumeBucket);
    }

    [Fact]
    public void Resumes_with_no_applications_still_get_a_row_with_zero_sent()
    {
        var row = Assert.Single(ResumeAnalyticsService.Build(new[] { Resume(1, 1) }, Array.Empty<JobApplication>()).Rows);
        Assert.Equal(0, row.Sent);
        Assert.Equal(0, row.ResponseRate);
        Assert.True(row.LowConfidence);
    }

    [Fact]
    public void Label_is_the_custom_label_or_the_file_name_without_extension()
    {
        var rows = ResumeAnalyticsService.Build(new[] { Resume(1, 1, label: "Backend v1"), Resume(2, 2, file: "Majd_Resume.pdf") }, Array.Empty<JobApplication>()).Rows;
        Assert.Contains(rows, r => r.Label == "Backend v1");
        Assert.Contains(rows, r => r.Label == "Majd_Resume");
    }

    // ── Exclusions ───────────────────────────────────────

    [Fact]
    public void Saved_applications_are_not_counted_anywhere()
    {
        var apps = Many(ApplicationStatus.Saved, 1, 3).Concat(new[] { App(ApplicationStatus.Applied, 1) });
        var row = Assert.Single(ResumeAnalyticsService.Build(new[] { Resume(1, 1) }, apps).Rows);
        Assert.Equal(1, row.Sent);
    }

    [Fact]
    public void Rejected_counts_as_sent_but_not_as_a_response()
    {
        var apps = new[] { App(ApplicationStatus.Rejected, 1), App(ApplicationStatus.Applied, 1) };
        var row = Assert.Single(ResumeAnalyticsService.Build(new[] { Resume(1, 1) }, apps).Rows);
        Assert.Equal(2, row.Sent);
        Assert.Equal(0, row.Responses);
    }

    // ── Rate math ────────────────────────────────────────

    [Fact]
    public void Interviews_and_offers_are_responses_and_the_rate_is_responses_over_sent()
    {
        var apps = Many(ApplicationStatus.Applied, 1, 5)
            .Concat(Many(ApplicationStatus.Rejected, 1, 3))
            .Concat(Many(ApplicationStatus.Interview, 1, 3))
            .Concat(Many(ApplicationStatus.Offer, 1, 1));   // 12 sent, 4 responses

        var row = Assert.Single(ResumeAnalyticsService.Build(new[] { Resume(1, 1) }, apps).Rows);

        Assert.Equal(12, row.Sent);
        Assert.Equal(3, row.Interviews);
        Assert.Equal(1, row.Offers);
        Assert.Equal(4, row.Responses);
        Assert.Equal(33.333, row.ResponseRate, 3);
        Assert.Equal(33, row.ResponseRatePercent);
    }

    [Fact]
    public void Average_match_score_ignores_unscored_applications_and_is_null_when_none_are_scored()
    {
        var apps = new[] { App(ApplicationStatus.Applied, 1, 80), App(ApplicationStatus.Applied, 1, 60), App(ApplicationStatus.Applied, 1), App(ApplicationStatus.Applied, 2) };
        var rows = ResumeAnalyticsService.Build(new[] { Resume(1, 1), Resume(2, 2) }, apps).Rows;

        Assert.Equal(70, rows.Single(r => r.ResumeVersionId == 1).AverageMatchScore);
        Assert.Null(rows.Single(r => r.ResumeVersionId == 2).AverageMatchScore);
    }

    [Fact]
    public void Confident_rows_come_first_by_rate_then_low_confidence_rows_by_rate()
    {
        var apps = Many(ApplicationStatus.Applied, 1, 4).Concat(Many(ApplicationStatus.Interview, 1, 1))   // 5 sent, 20%  (confident)
            .Concat(Many(ApplicationStatus.Applied, 2, 1)).Concat(Many(ApplicationStatus.Offer, 2, 1))      // 2 sent, 50%  (low confidence)
            .Concat(Many(ApplicationStatus.Applied, 3, 6))                                                   // 6 sent, 0%   (confident)
            .Concat(Many(ApplicationStatus.Offer, 4, 1)).Concat(Many(ApplicationStatus.Applied, 4, 3));     // 4 sent, 25%  (low confidence)

        var ids = ResumeAnalyticsService.Build(new[] { Resume(1, 1), Resume(2, 2), Resume(3, 3), Resume(4, 4) }, apps).Rows.Select(r => r.ResumeVersionId).ToList();

        Assert.Equal(new int?[] { 1, 3, 2, 4 }, ids);
    }

    [Fact]
    public void Within_the_same_confidence_band_ties_on_rate_are_broken_by_sent()
    {
        var apps = Many(ApplicationStatus.Applied, 1, 5).Concat(Many(ApplicationStatus.Interview, 1, 5))   // 10 sent, 50%
            .Concat(Many(ApplicationStatus.Applied, 2, 3)).Concat(Many(ApplicationStatus.Interview, 2, 3)); // 6 sent, 50%

        var ids = ResumeAnalyticsService.Build(new[] { Resume(1, 1), Resume(2, 2) }, apps).Rows.Select(r => r.ResumeVersionId).ToList();

        Assert.Equal(new int?[] { 1, 2 }, ids);
    }

    // ── Low confidence ───────────────────────────────────

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(17, false)]
    public void Low_confidence_below_five_applications(int sent, bool low)
    {
        var row = Assert.Single(ResumeAnalyticsService.Build(new[] { Resume(1, 1) }, Many(ApplicationStatus.Applied, 1, sent)).Rows);
        Assert.Equal(low, row.LowConfidence);
    }

    // ── Takeaway ─────────────────────────────────────────

    [Fact]
    public void Takeaway_names_the_best_resume_with_enough_applications()
    {
        var apps = Many(ApplicationStatus.Applied, 1, 13).Concat(Many(ApplicationStatus.Interview, 1, 3)).Concat(Many(ApplicationStatus.Offer, 1, 1))  // 4/17 = 24%
            .Concat(Many(ApplicationStatus.Offer, 2, 2));                                                                                            // 100% but only 2 sent

        var result = ResumeAnalyticsService.Build(new[] { Resume(1, 4, label: "Resume v4"), Resume(2, 5) }, apps);

        Assert.Equal("Resume v4 has the best response rate so far (24% across 17 applications).", result.Takeaway);
    }

    [Fact]
    public void Takeaway_ignores_the_no_resume_bucket()
    {
        var apps = Many(ApplicationStatus.Offer, null, 6).Concat(Many(ApplicationStatus.Applied, 1, 5)).Concat(Many(ApplicationStatus.Interview, 1, 1));

        var result = ResumeAnalyticsService.Build(new[] { Resume(1, 1, label: "Main") }, apps);

        Assert.StartsWith("Main has the best response rate so far (17% across 6 applications).", result.Takeaway);
    }

    [Fact]
    public void Takeaway_asks_for_more_data_when_no_resume_has_five_applications()
    {
        var apps = Many(ApplicationStatus.Offer, 1, 4).Concat(Many(ApplicationStatus.Offer, 2, 4));
        var result = ResumeAnalyticsService.Build(new[] { Resume(1, 1), Resume(2, 2) }, apps);

        Assert.Equal(ResumeAnalyticsService.NotEnoughData, result.Takeaway);
    }

    // ── Card visibility ──────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Card_shows_only_with_two_or_more_resume_versions(int resumes, bool show)
    {
        var versions = Enumerable.Range(1, resumes).Select(i => Resume(i, i));
        Assert.Equal(show, ResumeAnalyticsService.Build(versions, Array.Empty<JobApplication>()).ShowCard);
    }
}
