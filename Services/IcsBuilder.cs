using System.Text;
using InternTrackAI.Models;

namespace InternTrackAI.Services;

/// <summary>
/// Hand-rolled RFC 5545 writer for the calendar feed and the per-application download — no
/// package. Emits one VEVENT per Deadline (all-day), InterviewAt (timed, one hour, expressed in
/// UTC because no time zone is stored) and FollowUpAt (all-day). TEXT values are escaped
/// (backslash, semicolon, comma, newline) and every content line is folded at 75 octets.
/// </summary>
public static class IcsBuilder
{
    public const string ProdId  = "-//InternTrackAI//Reminders//EN";
    public const string UidHost = "interntrackai";
    public const int    MaxLineOctets = 75;

    /// <summary>One calendar entry. <see cref="Start"/>/<see cref="End"/> are dates for all-day events, UTC instants otherwise.</summary>
    public sealed record IcsEvent(string Uid, string Summary, string? Description, string? Url,
                                  DateTime Start, DateTime? End, bool AllDay, DateTime Stamp);

    /// <summary>Events for one application: deadline, interview, follow-up — whichever dates are set.</summary>
    public static IEnumerable<IcsEvent> EventsFor(JobApplication a, DateTime stampUtc)
    {
        var title = $"{a.CompanyName} — {a.RoleTitle}";
        var description = Description(a);

        if (a.Deadline.HasValue)
            yield return new IcsEvent($"deadline-{a.Id}@{UidHost}", $"Deadline: {title}", description, a.JobLink,
                                      a.Deadline.Value.Date, null, AllDay: true, stampUtc);

        if (a.InterviewAt.HasValue)
            yield return new IcsEvent($"interview-{a.Id}@{UidHost}", $"Interview: {title}", description, a.JobLink,
                                      a.InterviewAt.Value, a.InterviewAt.Value.AddHours(1), AllDay: false, stampUtc);

        if (a.FollowUpAt.HasValue)
            yield return new IcsEvent($"followup-{a.Id}@{UidHost}", $"Follow up: {title}", description, a.JobLink,
                                      a.FollowUpAt.Value.Date, null, AllDay: true, stampUtc);
    }

    private static string Description(JobApplication a)
    {
        var sb = new StringBuilder();
        sb.Append(a.RoleTitle).Append(" at ").Append(a.CompanyName);
        sb.Append("\nStatus: ").Append(a.Status);
        if (!string.IsNullOrWhiteSpace(a.Location)) sb.Append("\nLocation: ").Append(a.Location);
        if (!string.IsNullOrWhiteSpace(a.JobLink))  sb.Append("\nSource: ").Append(a.JobLink);
        return sb.ToString();
    }

    /// <summary>Serialises a whole VCALENDAR (CRLF line endings, folded).</summary>
    public static string Build(IEnumerable<IcsEvent> events, string calendarName = "InternTrackAI")
    {
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:" + ProdId,
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "X-WR-CALNAME:" + Escape(calendarName)
        };

        foreach (var e in events)
        {
            lines.Add("BEGIN:VEVENT");
            lines.Add("UID:" + e.Uid);
            lines.Add("DTSTAMP:" + Utc(e.Stamp));
            if (e.AllDay)
            {
                lines.Add("DTSTART;VALUE=DATE:" + e.Start.ToString("yyyyMMdd"));
                lines.Add("DTEND;VALUE=DATE:"   + e.Start.AddDays(1).ToString("yyyyMMdd"));
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

    private static string Utc(DateTime dt) => dt.ToString("yyyyMMdd'T'HHmmss'Z'");

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
