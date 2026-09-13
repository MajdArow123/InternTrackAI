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
}
