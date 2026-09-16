using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// Boundary tests for every ReminderService rule. The clock is fixed (a Wednesday at 10:00 UTC)
/// so "exactly N days" cases are deterministic.
/// </summary>
public class ReminderServiceTests
{
    private static readonly DateTime Now   = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = Now.Date;
    private const int Window = 7;

    private static JobApplication App(ApplicationStatus status = ApplicationStatus.Applied,
        int? appliedDaysAgo = null, int? deadlineInDays = null, int? interviewInDays = null,
        int? lastContactDaysAgo = null, int? followUpInDays = null, int id = 1) => new()
    {
        Id            = id,
        UserId        = "u",
        CompanyName   = "Co",
        RoleTitle     = "Intern",
        Status        = status,
        DateApplied   = appliedDaysAgo.HasValue    ? Today.AddDays(-appliedDaysAgo.Value)   : null,
        Deadline      = deadlineInDays.HasValue    ? Today.AddDays(deadlineInDays.Value)     : null,
        InterviewAt   = interviewInDays.HasValue   ? Today.AddDays(interviewInDays.Value).AddHours(14) : null,
        LastContactAt = lastContactDaysAgo.HasValue ? Today.AddDays(-lastContactDaysAgo.Value).AddHours(9) : null,
        FollowUpAt    = followUpInDays.HasValue    ? Today.AddDays(followUpInDays.Value)     : null
    };

    private static List<ReminderKind> Kinds(JobApplication a, int window = Window) =>
        ReminderService.Evaluate(a, window, Now).Select(i => i.Kind).ToList();

    // ── Follow-up due ─────────────────────────────────────

    [Theory]
    [InlineData(7, true)]   // exactly the window: due
    [InlineData(8, true)]
    [InlineData(6, false)]  // one day short: not due
    [InlineData(0, false)]  // applied today
    public void FollowUp_boundary_on_days_since_applied(int daysAgo, bool expected) =>
        Assert.Equal(expected, ReminderService.IsFollowUpDue(App(appliedDaysAgo: daysAgo), Today, Window));

    [Fact]
    public void FollowUp_honours_custom_window()
    {
        Assert.True(ReminderService.IsFollowUpDue(App(appliedDaysAgo: 3), Today, 3));
        Assert.False(ReminderService.IsFollowUpDue(App(appliedDaysAgo: 3), Today, 4));
    }

    [Theory]
    [InlineData(ApplicationStatus.Saved)]
    [InlineData(ApplicationStatus.Interview)]
    [InlineData(ApplicationStatus.Offer)]
    [InlineData(ApplicationStatus.Rejected)]
    public void FollowUp_only_applies_to_Applied(ApplicationStatus status) =>
        Assert.False(ReminderService.IsFollowUpDue(App(status, appliedDaysAgo: 30), Today, Window));

    [Fact]
    public void FollowUp_without_any_anchor_date_is_not_due() =>
        Assert.False(ReminderService.IsFollowUpDue(App(), Today, Window));

    [Fact]
    public void FollowUp_uses_last_contact_when_it_is_later_than_applied()
    {
        Assert.False(ReminderService.IsFollowUpDue(App(appliedDaysAgo: 20, lastContactDaysAgo: 2), Today, Window));
        Assert.True (ReminderService.IsFollowUpDue(App(appliedDaysAgo: 20, lastContactDaysAgo: 7), Today, Window));
        Assert.Equal("Last contact 7 days ago, no reply", ReminderService.FollowUpReason(App(appliedDaysAgo: 20, lastContactDaysAgo: 7), Today));
    }

    [Fact]
    public void FollowUp_snoozed_while_FollowUpAt_is_in_the_future_and_returns_after()
    {
        Assert.False(ReminderService.IsFollowUpDue(App(appliedDaysAgo: 10, followUpInDays: 3), Today, Window));
        Assert.False(ReminderService.IsFollowUpDue(App(appliedDaysAgo: 10, followUpInDays: 1), Today, Window));
        Assert.True (ReminderService.IsFollowUpDue(App(appliedDaysAgo: 10, followUpInDays: 0), Today, Window),  "snooze ending today is due again");
        Assert.True (ReminderService.IsFollowUpDue(App(appliedDaysAgo: 10, followUpInDays: -1), Today, Window));
    }

    [Fact]
    public void FollowUp_reason_counts_days_since_applied() =>
        Assert.Equal("Applied 9 days ago, no reply", ReminderService.FollowUpReason(App(appliedDaysAgo: 9), Today));

    // ── Deadline soon / overdue ───────────────────────────

    [Theory]
    [InlineData(0, true)]   // today
    [InlineData(7, true)]   // exactly 7 days
    [InlineData(8, false)]
    [InlineData(-1, false)] // past: not "soon" (overdue handles Saved)
    public void DeadlineSoon_boundaries(int inDays, bool expected) =>
        Assert.Equal(expected, ReminderService.IsDeadlineSoon(App(ApplicationStatus.Applied, deadlineInDays: inDays), Today));

    [Theory]
    [InlineData(ApplicationStatus.Offer)]
    [InlineData(ApplicationStatus.Rejected)]
    public void DeadlineSoon_excludes_decided_applications(ApplicationStatus status) =>
        Assert.False(ReminderService.IsDeadlineSoon(App(status, deadlineInDays: 2), Today));

    [Theory]
    [InlineData(ApplicationStatus.Saved)]
    [InlineData(ApplicationStatus.Applied)]
    [InlineData(ApplicationStatus.Interview)]
    public void DeadlineSoon_includes_open_statuses(ApplicationStatus status) =>
        Assert.True(ReminderService.IsDeadlineSoon(App(status, deadlineInDays: 2), Today));

    [Fact]
    public void Overdue_requires_past_deadline_and_Saved()
    {
        Assert.True (ReminderService.IsOverdue(App(ApplicationStatus.Saved, deadlineInDays: -1), Today));
        Assert.False(ReminderService.IsOverdue(App(ApplicationStatus.Saved, deadlineInDays: 0), Today), "today is not overdue");
        Assert.False(ReminderService.IsOverdue(App(ApplicationStatus.Applied, deadlineInDays: -5), Today));
        Assert.False(ReminderService.IsOverdue(App(ApplicationStatus.Rejected, deadlineInDays: -5), Today));
    }

    [Fact]
    public void Deadline_reasons()
    {
        Assert.Equal("Deadline today",           ReminderService.DeadlineReason(Today, Today));
        Assert.Equal("Deadline tomorrow",        ReminderService.DeadlineReason(Today.AddDays(1), Today));
        Assert.Equal("Deadline in 2 days",       ReminderService.DeadlineReason(Today.AddDays(2), Today));
        Assert.Equal("Deadline was yesterday",   ReminderService.DeadlineReason(Today.AddDays(-1), Today));
        Assert.Equal("Deadline was 3 days ago",  ReminderService.DeadlineReason(Today.AddDays(-3), Today));
    }

    // ── Interview ─────────────────────────────────────────

    [Theory]
    [InlineData(0, true)]
    [InlineData(14, true)]
    [InlineData(15, false)]
    [InlineData(-1, false)]
    public void Interview_boundaries(int inDays, bool expected) =>
        Assert.Equal(expected, ReminderService.IsUpcomingInterview(App(ApplicationStatus.Interview, interviewInDays: inDays), Today));

    [Fact]
    public void Interview_short_when_formats()
    {
        Assert.Equal("today 2:00 PM",    ReminderService.ShortWhen(Today.AddHours(14), Today));
        Assert.Equal("tomorrow 9:30 AM", ReminderService.ShortWhen(Today.AddDays(1).AddHours(9.5), Today));
        Assert.Equal("Sat 2:00 PM",      ReminderService.ShortWhen(Today.AddDays(3).AddHours(14), Today));
        Assert.Equal("Sep 25, 2:00 PM",  ReminderService.ShortWhen(Today.AddDays(9).AddHours(14), Today));
    }

    // ── Combination and ordering ──────────────────────────

    [Fact]
    public void Evaluate_can_return_more_than_one_kind_for_one_application()
    {
        var kinds = Kinds(App(ApplicationStatus.Applied, appliedDaysAgo: 10, deadlineInDays: 2, interviewInDays: 5));
        Assert.Equal(new[] { ReminderKind.DeadlineSoon, ReminderKind.FollowUpDue, ReminderKind.Interview }, kinds);
    }

    [Fact]
    public void Evaluate_returns_nothing_for_a_quiet_application() =>
        Assert.Empty(Kinds(App(ApplicationStatus.Applied, appliedDaysAgo: 2, deadlineInDays: 30)));

    [Fact]
    public void Build_sorts_overdue_first_then_by_date()
    {
        var apps = new[]
        {
            App(ApplicationStatus.Interview, interviewInDays: 3, id: 1),
            App(ApplicationStatus.Applied,   deadlineInDays: 1, id: 2),
            App(ApplicationStatus.Saved,     deadlineInDays: -2, id: 3),
            App(ApplicationStatus.Applied,   appliedDaysAgo: 9, id: 4),
            App(ApplicationStatus.Saved,     deadlineInDays: -9, id: 5),
        };

        var ordered = ReminderService.Build(apps, Window, Now).Select(i => (i.Application.Id, i.Kind)).ToList();

        Assert.Equal(new[]
        {
            (5, ReminderKind.Overdue),       // most overdue first
            (3, ReminderKind.Overdue),
            (4, ReminderKind.FollowUpDue),   // due date = applied + 7 = 2 days ago
            (2, ReminderKind.DeadlineSoon),  // tomorrow
            (1, ReminderKind.Interview),     // in 3 days
        }, ordered);
    }

    [Fact]
    public void Normalize_clamps_out_of_range_settings_to_default()
    {
        Assert.Equal(7,  ReminderService.Normalize(null));
        Assert.Equal(7,  ReminderService.Normalize(0));
        Assert.Equal(7,  ReminderService.Normalize(31));
        Assert.Equal(3,  ReminderService.Normalize(3));
        Assert.Equal(30, ReminderService.Normalize(30));
    }

    [Fact]
    public void DeadlineChip_windows_and_tones()
    {
        Assert.Null(ReminderService.DeadlineChip(App(deadlineInDays: 15), Today));
        Assert.Null(ReminderService.DeadlineChip(App(deadlineInDays: -1), Today));
        Assert.Equal(("Deadline in 2 days", "danger"),  ReminderService.DeadlineChip(App(deadlineInDays: 2), Today));
        Assert.Equal(("Deadline in 5 days", "warning"), ReminderService.DeadlineChip(App(deadlineInDays: 5), Today));
        Assert.Equal(("Deadline in 10 days", ""),       ReminderService.DeadlineChip(App(deadlineInDays: 10), Today));
    }

    // ── One "today" per user, whatever UTC says ───────────

    /// <summary>
    /// 8am and 8pm on the same Toronto day. At 8pm EDT it is already the next day in UTC, so anything that
    /// reached for <c>DateTime.UtcNow.Date</c> instead of the user's clock would silently age every
    /// application by a day for the last four hours of every evening.
    /// </summary>
    private static readonly UserClock TorontoMorning = UserClock.For("America/Toronto", new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
    private static readonly UserClock TorontoEvening = UserClock.For("America/Toronto", new DateTime(2026, 9, 16, 00, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Evening_and_morning_of_the_same_local_day_share_one_today()
    {
        Assert.Equal(new DateTime(2026, 9, 15), TorontoMorning.Today);
        Assert.Equal(new DateTime(2026, 9, 15), TorontoEvening.Today);      // 8pm EDT, already Sep 16 in UTC
        Assert.NotEqual(TorontoEvening.NowUtc.Date, TorontoEvening.Today);  // the trap this pins
    }

    [Theory]
    [InlineData(6, false)]   // a day short of the window, morning and evening alike
    [InlineData(7, true)]    // exactly the window — the boundary an off-by-one day would cross
    [InlineData(8, true)]
    public void Follow_up_due_does_not_change_between_morning_and_evening(int appliedDaysAgo, bool due)
    {
        // Built against the user's local today, the way a real row seeded "N days ago" would be.
        var app = new JobApplication
        {
            Id = 1, UserId = "u", CompanyName = "Co", RoleTitle = "Intern",
            Status = ApplicationStatus.Applied,
            DateApplied = TorontoMorning.Today.AddDays(-appliedDaysAgo)
        };

        Assert.Equal(due, ReminderService.IsFollowUpDue(app, TorontoMorning, Window));
        Assert.Equal(due, ReminderService.IsFollowUpDue(app, TorontoEvening, Window));
    }

    [Fact]
    public void Every_rule_and_its_wording_is_identical_morning_and_evening()
    {
        var today = TorontoMorning.Today;
        var apps = new[]
        {
            // One of each kind, each sitting on its boundary so a one-day drift would be visible.
            new JobApplication { Id = 1, UserId = "u", CompanyName = "Follow Co",   RoleTitle = "Intern", Status = ApplicationStatus.Applied, DateApplied = today.AddDays(-9) },
            new JobApplication { Id = 2, UserId = "u", CompanyName = "Overdue Co",  RoleTitle = "Intern", Status = ApplicationStatus.Saved,   Deadline = today.AddDays(-1) },
            new JobApplication { Id = 3, UserId = "u", CompanyName = "Deadline Co", RoleTitle = "Intern", Status = ApplicationStatus.Saved,   Deadline = today.AddDays(7) },
            new JobApplication { Id = 4, UserId = "u", CompanyName = "Interview Co",RoleTitle = "Intern", Status = ApplicationStatus.Interview,
                                 InterviewAt = TorontoMorning.ToUtc(today.AddDays(14).AddHours(10)) }
        };

        static List<string> Lines(IEnumerable<JobApplication> apps, UserClock clock) =>
            ReminderService.Build(apps, Window, clock).Select(i => $"{i.Application.Id}:{i.Kind}:{i.Reason}").ToList();

        var morning = Lines(apps, TorontoMorning);
        var evening = Lines(apps, TorontoEvening);

        Assert.Equal(4, morning.Count);
        Assert.Equal(morning, evening);
        Assert.Contains("1:FollowUpDue:Applied 9 days ago, no reply", morning);
        Assert.Contains("3:DeadlineSoon:Deadline in 7 days", morning);
    }

    [Fact]
    public void Analyzer_iso_date_parsing_is_strict()
    {
        Assert.Equal(new DateTime(2026, 10, 1), InternTrackAI.Models.ViewModels.JobAnalysisResult.ParseIsoDate("2026-10-01"));
        Assert.Null(InternTrackAI.Models.ViewModels.JobAnalysisResult.ParseIsoDate("October 1, 2026"));
        Assert.Null(InternTrackAI.Models.ViewModels.JobAnalysisResult.ParseIsoDate("2026-10"));
        Assert.Null(InternTrackAI.Models.ViewModels.JobAnalysisResult.ParseIsoDate(null));
    }
}
