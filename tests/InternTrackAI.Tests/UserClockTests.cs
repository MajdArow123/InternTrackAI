using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// The one UTC ↔ user-zone helper, pinned to fixed instants so DST edges are deterministic.
/// Toronto is UTC−4 in September (EDT) and UTC−5 in January (EST).
/// </summary>
public class UserClockTests
{
    private static readonly UserClock Toronto = UserClock.For("America/Toronto", new DateTime(2026, 9, 13, 21, 30, 0, DateTimeKind.Utc));
    private static readonly UserClock London  = UserClock.For("Europe/London",   new DateTime(2026, 9, 13, 21, 30, 0, DateTimeKind.Utc));

    [Fact]
    public void A_2pm_Toronto_interview_round_trips_through_utc_storage_and_back_to_2pm()
    {
        var typed  = new DateTime(2026, 9, 20, 14, 0, 0);            // what the form posts (Kind = Unspecified)
        var stored = Toronto.ToUtc(typed);

        Assert.Equal(new DateTime(2026, 9, 20, 18, 0, 0), stored);   // 2:00 PM EDT = 18:00Z
        Assert.Equal(DateTimeKind.Utc, stored.Kind);

        var shown = Toronto.ToLocal(stored);
        Assert.Equal(typed, shown);
        Assert.Equal("2026-09-20T14:00", Toronto.InputDateTime(stored));
        Assert.Equal("Sep 20, 2026 at 2:00 PM", Toronto.LocalDateTime(stored));
        Assert.Equal("2:00 PM", Toronto.LocalTime(stored));
    }

    [Fact]
    public void A_note_created_at_2123_utc_displays_as_523_pm_for_a_Toronto_user()
    {
        var created = new DateTime(2026, 9, 13, 21, 23, 0, DateTimeKind.Utc);
        Assert.Equal("Sep 13, 2026 at 5:23 PM", Toronto.LocalDateTime(created));
        Assert.Equal("Sep 13, 2026 at 10:23 PM", London.LocalDateTime(created));
        Assert.Equal("Sep 13, 2026 at 9:23 PM", UserClock.Utc().LocalDateTime(created));
    }

    [Fact]
    public void The_same_interview_reads_2pm_in_Toronto_and_7pm_in_London()
    {
        var stored = new DateTime(2026, 9, 20, 18, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2:00 PM", Toronto.LocalTime(stored));
        Assert.Equal("7:00 PM", London.LocalTime(stored));
    }

    [Fact]
    public void Today_is_the_users_local_date_not_the_servers()
    {
        var lateEveningToronto = UserClock.For("America/Toronto", new DateTime(2026, 9, 14, 2, 0, 0, DateTimeKind.Utc)); // 10 PM Sep 13 in Toronto
        Assert.Equal(new DateTime(2026, 9, 13), lateEveningToronto.Today);
        Assert.Equal(new DateTime(2026, 9, 14), UserClock.Utc(new DateTime(2026, 9, 14, 2, 0, 0, DateTimeKind.Utc)).Today);
        Assert.Equal(1, lateEveningToronto.DaysUntil(new DateTime(2026, 9, 14)));
    }

    [Fact]
    public void Winter_uses_standard_time_and_the_spring_forward_gap_moves_an_hour_later()
    {
        Assert.Equal(new DateTime(2026, 1, 15, 19, 0, 0), Toronto.ToUtc(new DateTime(2026, 1, 15, 14, 0, 0)));   // EST = UTC−5
        // 2:30 AM does not exist on 2026-03-08 in Toronto; it is read as 3:30 EDT (07:30Z).
        Assert.Equal(new DateTime(2026, 3, 8, 7, 30, 0), Toronto.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0)));
    }

    [Fact]
    public void Calendar_dates_are_never_shifted_and_instants_are()
    {
        var deadline = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);   // a Deadline as EF hands it back
        Assert.Equal("Oct 1, 2026", Toronto.Date(deadline));
        Assert.Equal("Sep 30, 2026", Toronto.LocalDate(deadline));             // what shifting would have done
        Assert.Equal("—", Toronto.Date(null, "—"));
        Assert.Equal("", Toronto.LocalDateTime(null));
    }

    [Fact]
    public void Unknown_or_blank_zone_ids_fall_back_to_Toronto_and_UTC_is_recognised()
    {
        Assert.Equal("America/Toronto", UserClock.For(null).ZoneId);
        Assert.Equal("America/Toronto", UserClock.For("Mars/Olympus_Mons").ZoneId);
        Assert.Equal("Europe/London",   UserClock.For("Europe/London").ZoneId);
        Assert.True(UserClock.Utc().IsUtc);
        Assert.True(UserClock.For("UTC").IsUtc);
        Assert.False(Toronto.IsUtc);
        Assert.True(TimeZones.IsValid("Asia/Tokyo"));
        Assert.False(TimeZones.IsValid("Mars/Olympus_Mons"));
        Assert.False(TimeZones.IsValid(""));
    }

    [Fact]
    public void Dropdown_groups_by_region_with_offset_labels_and_keeps_the_current_zone_selectable()
    {
        var groups = TimeZones.Groups(new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc), "America/Toronto");

        Assert.Equal("UTC", groups[0].Region);
        var america = Assert.Single(groups, g => g.Region == "America");
        var toronto = Assert.Single(america.Zones, z => z.Id == "America/Toronto");
        Assert.Equal("Toronto (UTC−04:00)", toronto.Label);
        Assert.Contains(groups, g => g.Region == "Europe" && g.Zones.Any(z => z.Id == "Europe/London"));
        Assert.DoesNotContain(groups.SelectMany(g => g.Zones), z => z.Id.StartsWith("Etc/") || z.Id.StartsWith("US/"));
        Assert.All(groups.Skip(1), g => Assert.Equal(g.Zones.Select(z => z.Label).OrderBy(l => l, StringComparer.Ordinal), g.Zones.Select(z => z.Label)));
        Assert.DoesNotContain(groups, g => g.Region == "Other");
    }

    // ── ReminderService reads instants in the user's zone ──

    private static JobApplication Interview(DateTime atUtc) => new()
    {
        Id = 1, UserId = "u", CompanyName = "Co", RoleTitle = "Intern", Status = ApplicationStatus.Interview, InterviewAt = atUtc
    };

    [Fact]
    public void Interview_reason_and_window_use_the_users_local_day()
    {
        var now = new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);
        var app = Interview(new DateTime(2026, 9, 17, 1, 0, 0, DateTimeKind.Utc));   // 9:00 PM Sep 16 in Toronto, 1:00 AM Sep 17 in UTC

        var toronto = ReminderService.Evaluate(app, 7, UserClock.For("America/Toronto", now)).Single();
        var utc     = ReminderService.Evaluate(app, 7, now).Single();

        Assert.Equal("Interview today 9:00 PM", toronto.Reason);
        Assert.Equal(new DateTime(2026, 9, 16, 21, 0, 0), toronto.When);
        Assert.Equal("Interview tomorrow 1:00 AM", utc.Reason);

        // 15 days out in UTC but still day 14 in Toronto (UTC−4): only the Toronto clock shows it.
        var edge = Interview(new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc));
        Assert.True (ReminderService.IsUpcomingInterview(edge, UserClock.For("America/Toronto", now)));
        Assert.False(ReminderService.IsUpcomingInterview(edge, now.Date));
    }

    [Fact]
    public void Snooze_boundary_is_judged_on_the_users_local_date()
    {
        // FollowUpAt = Toronto midnight Sep 17 (04:00Z). At 23:00 Toronto on Sep 16 (03:00Z Sep 17) the snooze still holds
        // for a Toronto user, while a zone-less clock already sees Sep 17 and calls it due.
        var now = new DateTime(2026, 9, 17, 3, 0, 0, DateTimeKind.Utc);
        var app = new JobApplication
        {
            Id = 1, UserId = "u", CompanyName = "Co", RoleTitle = "Intern", Status = ApplicationStatus.Applied,
            DateApplied = new DateTime(2026, 9, 1), FollowUpAt = new DateTime(2026, 9, 17, 4, 0, 0, DateTimeKind.Utc)
        };
        Assert.False(ReminderService.IsFollowUpDue(app, UserClock.For("America/Toronto", now), 7));
        Assert.True (ReminderService.IsFollowUpDue(app, now.Date, 7));
    }

    [Fact]
    public void Last_contact_days_ago_counts_from_the_local_date()
    {
        // Contacted at 02:00Z Sep 10 = 10:00 PM Sep 9 in Toronto. Now = Sep 16 10:00Z.
        var now = new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);
        var app = new JobApplication
        {
            Id = 1, UserId = "u", CompanyName = "Co", RoleTitle = "Intern", Status = ApplicationStatus.Applied,
            DateApplied = new DateTime(2026, 8, 1), LastContactAt = new DateTime(2026, 9, 10, 2, 0, 0, DateTimeKind.Utc)
        };
        Assert.Equal("Last contact 7 days ago, no reply", ReminderService.FollowUpReason(app, UserClock.For("America/Toronto", now)));
        Assert.Equal("Last contact 6 days ago, no reply", ReminderService.FollowUpReason(app, now.Date));
    }
}
