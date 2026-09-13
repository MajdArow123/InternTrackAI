using System.Text;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

public class IcsBuilderTests
{
    private static readonly DateTime Stamp = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static JobApplication App() => new()
    {
        Id = 42, UserId = "u", CompanyName = "Shopify", RoleTitle = "Backend Developer Intern",
        Status = ApplicationStatus.Interview, Location = "Toronto, ON",
        JobLink = "https://www.shopify.com/careers/backend-intern?src=a,b",
        Deadline = new DateTime(2026, 10, 1), InterviewAt = new DateTime(2026, 9, 16, 14, 0, 0), FollowUpAt = new DateTime(2026, 9, 20)
    };

    [Fact]
    public void Escape_handles_backslash_semicolon_comma_and_newlines()
    {
        Assert.Equal(@"a\\b\;c\,d\ne\nf", IcsBuilder.Escape("a\\b;c,d\r\ne\nf"));
    }

    [Fact]
    public void Fold_keeps_every_physical_line_within_75_octets_and_unfolds_back()
    {
        var title = "SUMMARY:Interview: " + string.Join(", ", Enumerable.Range(1, 12).Select(i => $"Very Long Company Name {i}")) + " — Backend Developer Intern, Payments";
        var escaped = "SUMMARY:" + IcsBuilder.Escape(title["SUMMARY:".Length..]);

        var folded = IcsBuilder.Fold(escaped);
        var physical = folded.Split("\r\n");

        Assert.True(physical.Length > 3, "a long line must be split");
        Assert.All(physical, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75, $"line has {Encoding.UTF8.GetByteCount(l)} octets: {l}"));
        Assert.All(physical.Skip(1), l => Assert.StartsWith(" ", l));
        Assert.Equal(escaped, folded.Replace("\r\n ", ""));            // unfolding restores the logical line
        Assert.Contains("\\,", folded.Replace("\r\n ", ""));           // commas are escaped inside the folded text
    }

    [Fact]
    public void Fold_never_splits_a_multibyte_character()
    {
        var line = "DESCRIPTION:" + new string('é', 120);   // 2 octets each
        var folded = IcsBuilder.Fold(line);
        foreach (var l in folded.Split("\r\n"))
            Assert.True(Encoding.UTF8.GetByteCount(l) <= 75);
        Assert.Equal(line, folded.Replace("\r\n ", ""));
    }

    [Fact]
    public void Fold_leaves_short_lines_alone() =>
        Assert.Equal("UID:deadline-1@interntrackai", IcsBuilder.Fold("UID:deadline-1@interntrackai"));

    [Fact]
    public void EventsFor_emits_deadline_interview_and_follow_up_with_stable_uids()
    {
        var events = IcsBuilder.EventsFor(App(), Stamp).ToList();

        Assert.Equal(new[] { "deadline-42@interntrackai", "interview-42@interntrackai", "followup-42@interntrackai" }, events.Select(e => e.Uid));
        Assert.Equal("Interview: Shopify — Backend Developer Intern", events[1].Summary);
        Assert.True(events[0].AllDay);
        Assert.False(events[1].AllDay);
        Assert.Equal(events[1].Start.AddHours(1), events[1].End);
        Assert.All(events, e => Assert.Contains("Source: https://www.shopify.com/careers/backend-intern?src=a,b", e.Description));
    }

    [Fact]
    public void EventsFor_skips_dates_that_are_not_set()
    {
        var app = App(); app.Deadline = null; app.FollowUpAt = null;
        Assert.Single(IcsBuilder.EventsFor(app, Stamp));
    }

    [Fact]
    public void Build_writes_a_valid_calendar_with_crlf_all_day_and_timed_events()
    {
        var ics = IcsBuilder.Build(IcsBuilder.EventsFor(App(), Stamp));

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//InternTrackAI//Reminders//EN\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Equal(3, ics.Split("BEGIN:VEVENT").Length - 1);
        Assert.Contains("DTSTART;VALUE=DATE:20261001\r\nDTEND;VALUE=DATE:20261002\r\n", ics);
        Assert.Contains("DTSTART:20260916T140000Z\r\nDTEND:20260916T150000Z\r\n", ics);
        Assert.Contains("DTSTAMP:20260913T120000Z", ics);
        Assert.Contains("SUMMARY:Deadline: Shopify — Backend Developer Intern", ics);
        Assert.Contains("URL:https://www.shopify.com/careers/backend-intern?src=a,b", ics);
        Assert.DoesNotContain("\n\n", ics);
        Assert.All(ics.Split("\r\n"), l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.DoesNotContain(ics.Replace("\r\n", ""), "\n");         // CRLF only
    }

    // ── Per-user zone: TZID + VTIMEZONE ──

    private static readonly UserClock Toronto = UserClock.For("America/Toronto", Stamp);

    [Fact]
    public void Interview_is_written_as_local_time_with_TZID_and_a_VTIMEZONE_block()
    {
        var app = App(); app.InterviewAt = new DateTime(2026, 9, 16, 18, 0, 0, DateTimeKind.Utc);   // 2:00 PM EDT
        var ics = IcsBuilder.Build(IcsBuilder.EventsFor(app, Stamp, Toronto));

        Assert.Contains("DTSTART;TZID=America/Toronto:20260916T140000\r\nDTEND;TZID=America/Toronto:20260916T150000\r\n", ics);
        Assert.DoesNotContain("T140000Z", ics);
        Assert.DoesNotContain("T180000Z", ics);

        Assert.Contains("BEGIN:VTIMEZONE\r\nTZID:America/Toronto\r\n", ics);
        Assert.Contains("BEGIN:DAYLIGHT\r\nDTSTART:20260308T020000\r\nTZOFFSETFROM:-0500\r\nTZOFFSETTO:-0400\r\n", ics);
        Assert.Contains("BEGIN:STANDARD\r\nDTSTART:20261101T020000\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\n", ics);
        Assert.True(ics.IndexOf("END:VTIMEZONE", StringComparison.Ordinal) < ics.IndexOf("BEGIN:VEVENT", StringComparison.Ordinal), "VTIMEZONE comes before the events");
        Assert.Equal(1, ics.Split("BEGIN:VTIMEZONE").Length - 1);

        // All-day events are untouched by TZID: the deadline is a calendar date and stays Oct 1; the follow-up is a
        // stored instant (here midnight UTC Sep 20 = 8 PM Sep 19 in Toronto), so its all-day date is the local one.
        Assert.Contains("DTSTART;VALUE=DATE:20261001\r\n", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20260919\r\n", ics);
        Assert.All(ics.Split("\r\n"), l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
    }

    [Fact]
    public void Follow_up_all_day_date_is_the_users_local_date()
    {
        var app = App(); app.FollowUpAt = new DateTime(2026, 9, 20, 4, 0, 0, DateTimeKind.Utc);   // Toronto midnight Sep 20
        var ics = IcsBuilder.Build(IcsBuilder.EventsFor(app, Stamp, Toronto));
        Assert.Contains("UID:followup-42@interntrackai\r\nDTSTAMP:20260913T120000Z\r\nDTSTART;VALUE=DATE:20260920\r\n", ics);
    }

    [Fact]
    public void A_UTC_clock_keeps_the_Z_form_and_writes_no_VTIMEZONE()
    {
        var ics = IcsBuilder.Build(IcsBuilder.EventsFor(App(), Stamp, UserClock.Utc(Stamp)));
        Assert.Contains("DTSTART:20260916T140000Z", ics);
        Assert.DoesNotContain("VTIMEZONE", ics);
        Assert.DoesNotContain("TZID", ics);
    }

    [Fact]
    public void Zone_without_daylight_saving_gets_a_single_standard_observance()
    {
        var lines = IcsBuilder.VTimeZone("Asia/Tokyo", 2026, 2026);
        Assert.Equal(new[] { "BEGIN:VTIMEZONE", "TZID:Asia/Tokyo", "BEGIN:STANDARD", "DTSTART:20260101T090000", "TZOFFSETFROM:+0900", "TZOFFSETTO:+0900" }, lines.Take(6));
        Assert.Equal("END:VTIMEZONE", lines[^1]);
        Assert.Equal(1, lines.Count(l => l.StartsWith("BEGIN:STANDARD")));
        Assert.DoesNotContain(lines, l => l.StartsWith("BEGIN:DAYLIGHT"));
    }

    [Fact]
    public void Transitions_finds_both_Toronto_clock_changes_in_2026_to_the_minute()
    {
        var tz = TimeZones.Resolve("America/Toronto");
        var t  = IcsBuilder.Transitions(tz, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToList();

        Assert.Equal(2, t.Count);
        Assert.Equal((new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc),  TimeSpan.FromHours(-5), TimeSpan.FromHours(-4)), t[0]);
        Assert.Equal((new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(-4), TimeSpan.FromHours(-5)), t[1]);
    }

    [Fact]
    public void Offset_text_is_RFC_5545_utc_offset() 
    {
        Assert.Equal("-0400", IcsBuilder.OffsetText(TimeSpan.FromHours(-4)));
        Assert.Equal("+0530", IcsBuilder.OffsetText(TimeSpan.FromHours(5.5)));
        Assert.Equal("+0000", IcsBuilder.OffsetText(TimeSpan.Zero));
    }
}
