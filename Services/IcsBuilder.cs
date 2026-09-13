using System.Text;
using InternTrackAI.Models;

namespace InternTrackAI.Services;

/// <summary>
/// Hand-rolled RFC 5545 writer for the calendar feed and the per-application download — no
/// package. Emits one VEVENT per Deadline (all-day), InterviewAt (timed, one hour) and FollowUpAt
/// (all-day). Timed events are written as local wall-clock time with <c>TZID=&lt;zone&gt;</c> and a
/// matching VTIMEZONE component (built from the host's tz data by <see cref="VTimeZone"/>), so
/// Google and Apple Calendar show the hour the user typed; a UTC clock falls back to the <c>Z</c>
/// form. TEXT values are escaped (backslash, semicolon, comma, newline) and every content line
/// is folded at 75 octets.
/// </summary>
public static class IcsBuilder
{
    public const string ProdId  = "-//InternTrackAI//Reminders//EN";
    public const string UidHost = "interntrackai";
    public const int    MaxLineOctets = 75;

    /// <summary>
    /// One calendar entry. <see cref="Start"/>/<see cref="End"/> are dates for all-day events; for timed
    /// events they are wall-clock times in <see cref="TimeZoneId"/>, or UTC instants when it is null.
    /// </summary>
    public sealed record IcsEvent(string Uid, string Summary, string? Description, string? Url,
                                  DateTime Start, DateTime? End, bool AllDay, DateTime Stamp, string? TimeZoneId = null);

    /// <summary>Events for one application in the user's zone: deadline, interview, follow-up — whichever dates are set.</summary>
    public static IEnumerable<IcsEvent> EventsFor(JobApplication a, DateTime stampUtc, UserClock clock)
    {
        var title = $"{a.CompanyName} — {a.RoleTitle}";
        var description = Description(a);
        var tzid = clock.IsUtc ? null : clock.ZoneId;

        // Deadline is a calendar date (no zone); interview and follow-up are stored instants.
        if (a.Deadline.HasValue)
            yield return new IcsEvent($"deadline-{a.Id}@{UidHost}", $"Deadline: {title}", description, a.JobLink,
                                      a.Deadline.Value.Date, null, AllDay: true, stampUtc);

        if (a.InterviewAt.HasValue)
        {
            var start = clock.ToLocal(a.InterviewAt.Value);
            yield return new IcsEvent($"interview-{a.Id}@{UidHost}", $"Interview: {title}", description, a.JobLink,
                                      start, start.AddHours(1), AllDay: false, stampUtc, tzid);
        }

        if (a.FollowUpAt.HasValue)
            yield return new IcsEvent($"followup-{a.Id}@{UidHost}", $"Follow up: {title}", description, a.JobLink,
                                      clock.ToLocal(a.FollowUpAt.Value).Date, null, AllDay: true, stampUtc);
    }

    /// <summary>Zone-less form: timed events are emitted as UTC (<c>Z</c>).</summary>
    public static IEnumerable<IcsEvent> EventsFor(JobApplication a, DateTime stampUtc) =>
        EventsFor(a, stampUtc, UserClock.Utc(stampUtc));

    private static string Description(JobApplication a)
    {
        var sb = new StringBuilder();
        sb.Append(a.RoleTitle).Append(" at ").Append(a.CompanyName);
        sb.Append("\nStatus: ").Append(a.Status);
        if (!string.IsNullOrWhiteSpace(a.Location)) sb.Append("\nLocation: ").Append(a.Location);
        if (!string.IsNullOrWhiteSpace(a.JobLink))  sb.Append("\nSource: ").Append(a.JobLink);
        return sb.ToString();
    }

    /// <summary>Serialises a whole VCALENDAR (CRLF line endings, folded). One VTIMEZONE per zone used by a timed event, before the events.</summary>
    public static string Build(IEnumerable<IcsEvent> events, string calendarName = "InternTrackAI")
    {
        var list  = events.ToList();
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:" + ProdId,
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "X-WR-CALNAME:" + Escape(calendarName)
        };

        foreach (var zone in list.Where(e => !e.AllDay && e.TimeZoneId != null).GroupBy(e => e.TimeZoneId!))
        {
            var years = zone.SelectMany(e => new[] { e.Start.Year, (e.End ?? e.Start).Year }).ToList();
            lines.AddRange(VTimeZone(zone.Key, years.Min(), years.Max()));
        }

        foreach (var e in list)
        {
            lines.Add("BEGIN:VEVENT");
            lines.Add("UID:" + e.Uid);
            lines.Add("DTSTAMP:" + Utc(e.Stamp));
            if (e.AllDay)
            {
                lines.Add("DTSTART;VALUE=DATE:" + e.Start.ToString("yyyyMMdd"));
                lines.Add("DTEND;VALUE=DATE:"   + e.Start.AddDays(1).ToString("yyyyMMdd"));
            }
            else if (e.TimeZoneId != null)
            {
                lines.Add($"DTSTART;TZID={e.TimeZoneId}:" + Local(e.Start));
                lines.Add($"DTEND;TZID={e.TimeZoneId}:"   + Local(e.End ?? e.Start.AddHours(1)));
            }
            else
            {
                lines.Add("DTSTART:" + Utc(e.Start));
                lines.Add("DTEND:"   + Utc(e.End ?? e.Start.AddHours(1)));
            }
            lines.Add("SUMMARY:" + Escape(e.Summary));
            if (!string.IsNullOrWhiteSpace(e.Description))
                lines.Add("DESCRIPTION:" + Escape(e.Description));
            if (Uri.TryCreate(e.Url, UriKind.Absolute, out var url) && (url.Scheme == "http" || url.Scheme == "https"))
                lines.Add("URL:" + url.AbsoluteUri);
            lines.Add("END:VEVENT");
        }

        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }

    private static string Utc(DateTime dt)   => dt.ToString("yyyyMMdd'T'HHmmss'Z'");
    private static string Local(DateTime dt) => dt.ToString("yyyyMMdd'T'HHmmss");

    /// <summary>
    /// RFC 5545 §3.6.5 VTIMEZONE for <paramref name="zoneId"/> covering <paramref name="fromYear"/>
    /// through <paramref name="toYear"/>. Rather than reverse-engineering yearly RRULEs from
    /// <see cref="TimeZoneInfo.AdjustmentRule"/> (whose shape differs per OS), it lists every actual
    /// offset change in the window as its own STANDARD / DAYLIGHT observance, found by scanning the
    /// host's tz data — correct for any zone, including ones with no DST or irregular history. The
    /// first observance is the offset in force on Jan 1 of <paramref name="fromYear"/>, so every event
    /// in the window has a rule to resolve against.
    /// </summary>
    public static IReadOnlyList<string> VTimeZone(string zoneId, int fromYear, int toYear)
    {
        var tz    = TimeZones.Resolve(zoneId);
        var lines = new List<string> { "BEGIN:VTIMEZONE", "TZID:" + zoneId };

        var start = new DateTime(fromYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(toYear + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var initial = tz.GetUtcOffset(start);
        Observance(lines, tz, start, initial, initial);

        foreach (var (atUtc, from, to) in Transitions(tz, start, end))
            Observance(lines, tz, atUtc, from, to);

        lines.Add("END:VTIMEZONE");
        return lines;
    }

    /// <summary>Every instant in [start, end) where the zone's UTC offset changes, resolved to the minute.</summary>
    public static IEnumerable<(DateTime AtUtc, TimeSpan From, TimeSpan To)> Transitions(TimeZoneInfo tz, DateTime start, DateTime end)
    {
        var step = TimeSpan.FromHours(1);
        var prev = tz.GetUtcOffset(start);
        for (var t = start; t < end; t += step)
        {
            var next = tz.GetUtcOffset(t + step);
            if (next == prev) continue;

            // Binary-search the hour down to the minute the offset flips (some zones switch at :30).
            DateTime lo = t, hi = t + step;
            while ((hi - lo) > TimeSpan.FromMinutes(1))
            {
                var mid = lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);
                mid = new DateTime(mid.Ticks - mid.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
                if (tz.GetUtcOffset(mid) == prev) lo = mid; else hi = mid;
            }
            yield return (hi, prev, next);
            prev = next;
        }
    }

    private static void Observance(List<string> lines, TimeZoneInfo tz, DateTime atUtc, TimeSpan from, TimeSpan to)
    {
        var daylight = tz.SupportsDaylightSavingTime && tz.IsDaylightSavingTime(atUtc.AddMinutes(1));
        var name     = daylight ? tz.DaylightName : tz.StandardName;
        lines.Add(daylight ? "BEGIN:DAYLIGHT" : "BEGIN:STANDARD");
        // Onset is written in the wall-clock time that was showing just before the change (TZOFFSETFROM).
        lines.Add("DTSTART:" + Local(atUtc + from));
        lines.Add("TZOFFSETFROM:" + OffsetText(from));
        lines.Add("TZOFFSETTO:" + OffsetText(to));
        if (!string.IsNullOrWhiteSpace(name)) lines.Add("TZNAME:" + Escape(name));
        lines.Add(daylight ? "END:DAYLIGHT" : "END:STANDARD");
    }

    /// <summary>"-0400", "+0530", "+0000" (RFC 5545 UTC-OFFSET).</summary>
    public static string OffsetText(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return $"{sign}{offset.Hours:00}{offset.Minutes:00}";
    }

    /// <summary>RFC 5545 §3.3.11 TEXT escaping: backslash, semicolon, comma, and newlines (as literal \n).</summary>
    public static string Escape(string value) =>
        value.Replace("\\", "\\\\")
             .Replace(";", "\\;")
             .Replace(",", "\\,")
             .Replace("\r\n", "\n")
             .Replace('\r', '\n')
             .Replace("\n", "\\n");

    /// <summary>
    /// RFC 5545 §3.1 line folding: at most 75 octets per physical line, continuation lines start
    /// with a single space (which counts toward their 75). Splits between UTF-8 sequences only.
    /// </summary>
    public static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= MaxLineOctets) return line;

        var sb = new StringBuilder(line.Length + 16);
        int used = 0, limit = MaxLineOctets;
        foreach (var rune in line.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (used + n > limit)
            {
                sb.Append("\r\n ");
                used  = 0;
                limit = MaxLineOctets - 1; // the leading space is one octet
            }
            sb.Append(rune.ToString());
            used += n;
        }
        return sb.ToString();
    }
}
