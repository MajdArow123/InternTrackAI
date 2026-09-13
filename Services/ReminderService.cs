using System.Globalization;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>Why an application needs attention. Declaration order is urgency order.</summary>
public enum ReminderKind
{
    /// <summary>Deadline already passed and the application is still only Saved.</summary>
    Overdue,
    /// <summary>Deadline within <see cref="ReminderService.DeadlineWindowDays"/> days, not yet Offer/Rejected.</summary>
    DeadlineSoon,
    /// <summary>Applied, no reply, and the follow-up window has elapsed.</summary>
    FollowUpDue,
    /// <summary>Interview scheduled within <see cref="ReminderService.InterviewWindowDays"/> days.</summary>
    Interview
}

/// <summary>One line of the "Attention" list: which application, why, and the date that drives it.</summary>
public sealed record ReminderItem(JobApplication Application, ReminderKind Kind, DateTime When, string Reason)
{
    /// <summary>Colour token for the left indicator / chip: danger, warning, or accent.</summary>
    public string Tone => Kind switch
    {
        ReminderKind.Overdue   => "danger",
        ReminderKind.Interview => "accent",
        _                      => "warning"
    };

    public string Label => Kind switch
    {
        ReminderKind.Overdue      => "Overdue",
        ReminderKind.DeadlineSoon => "Deadline",
        ReminderKind.FollowUpDue  => "Follow up",
        ReminderKind.Interview    => "Interview",
        _                         => Kind.ToString()
    };
}

/// <summary>
/// The one place that decides what "needs attention": follow-up due, deadline soon, overdue, and
/// upcoming interview. The dashboard Attention card, the board chips, the list's "Needs attention"
/// filter, the drawer and the calendar feed all call into here, so a rule change lands everywhere
/// at once and no view re-derives dates on its own. The rule methods are pure and take a
/// <see cref="UserClock"/> so they can be unit-tested at exact boundaries: "today" is the user's
/// local date and instants (InterviewAt, FollowUpAt, LastContactAt) are read in the user's zone,
/// while calendar dates (Deadline, DateApplied) are compared as-is. The <c>DateTime</c> overloads
/// are the zone-less form (a UTC clock) kept for callers and tests that predate per-user zones.
/// </summary>
public class ReminderService
{
    public const int DefaultFollowUpAfterDays = 7;
    public const int MinFollowUpAfterDays     = 3;
    public const int MaxFollowUpAfterDays     = 30;
    public const int DeadlineWindowDays       = 7;
    public const int InterviewWindowDays      = 14;
    public const int SnoozeDays               = 3;

    private readonly ApplicationDbContext _db;
    private readonly UserClockProvider _clocks;

    public ReminderService(ApplicationDbContext db, UserClockProvider clocks)
    {
        _db     = db;
        _clocks = clocks;
    }

    // ── Per-user entry points ─────────────────────────────

    /// <summary>The user's follow-up window, falling back to the default when unset or out of range.</summary>
    public async Task<int> FollowUpAfterDaysAsync(string userId)
    {
        var stored = await _db.UserProfiles
            .Where(p => p.UserId == userId)
            .Select(p => (int?)p.FollowUpAfterDays)
            .FirstOrDefaultAsync();
        return Normalize(stored);
    }

    /// <summary>Every reminder for the user, most urgent first, judged against the user's own clock (their zone's "today").</summary>
    public async Task<List<ReminderItem>> ForUserAsync(string userId, UserClock? clock = null)
    {
        var apps = await _db.JobApplications.Where(a => a.UserId == userId).ToListAsync();
        return Build(apps, await FollowUpAfterDaysAsync(userId), clock ?? await _clocks.ForUserAsync(userId));
    }

    /// <summary>Clamps a stored/posted value into the allowed range; null or out-of-range → default.</summary>
    public static int Normalize(int? value) =>
        value is >= MinFollowUpAfterDays and <= MaxFollowUpAfterDays ? value.Value : DefaultFollowUpAfterDays;

    // ── Pure rules ────────────────────────────────────────

    /// <summary>All reminders for a set of applications, sorted overdue first, then by date. <c>When</c> values are in the clock's zone.</summary>
    public static List<ReminderItem> Build(IEnumerable<JobApplication> apps, int followUpAfterDays, UserClock clock) =>
        apps.SelectMany(a => Evaluate(a, followUpAfterDays, clock))
            .OrderBy(i => i.Kind == ReminderKind.Overdue ? 0 : 1)
            .ThenBy(i => i.When)
            .ThenBy(i => i.Application.Id)
            .ToList();

    public static List<ReminderItem> Build(IEnumerable<JobApplication> apps, int followUpAfterDays, DateTime nowUtc) =>
        Build(apps, followUpAfterDays, UserClock.Utc(nowUtc));

    /// <summary>Reminders for one application (an application can have more than one, e.g. deadline soon and follow-up due).</summary>
    public static IEnumerable<ReminderItem> Evaluate(JobApplication a, int followUpAfterDays, UserClock clock)
    {
        var today = clock.Today;

        if (IsOverdue(a, today))
            yield return new ReminderItem(a, ReminderKind.Overdue, a.Deadline!.Value.Date, DeadlineReason(a.Deadline.Value, today));
        else if (IsDeadlineSoon(a, today))
            yield return new ReminderItem(a, ReminderKind.DeadlineSoon, a.Deadline!.Value.Date, DeadlineReason(a.Deadline.Value, today));

        if (IsFollowUpDue(a, clock, followUpAfterDays))
            yield return new ReminderItem(a, ReminderKind.FollowUpDue, FollowUpDueDate(a, followUpAfterDays, clock)!.Value, FollowUpReason(a, clock));

        if (IsUpcomingInterview(a, clock))
        {
            var at = clock.ToLocal(a.InterviewAt!.Value);
            yield return new ReminderItem(a, ReminderKind.Interview, at, "Interview " + ShortWhen(at, today));
        }
    }

    public static IEnumerable<ReminderItem> Evaluate(JobApplication a, int followUpAfterDays, DateTime nowUtc) =>
        Evaluate(a, followUpAfterDays, UserClock.Utc(nowUtc));

    /// <summary>Deadline within the next 7 days (today and day 7 included) and the application isn't already decided.</summary>
    public static bool IsDeadlineSoon(JobApplication a, DateTime today)
    {
        if (!a.Deadline.HasValue) return false;
        if (a.Status is ApplicationStatus.Offer or ApplicationStatus.Rejected) return false;
        var days = (a.Deadline.Value.Date - today).Days;
        return days >= 0 && days <= DeadlineWindowDays;
    }

    /// <summary>Deadline is in the past and the application was never applied to (still Saved).</summary>
    public static bool IsOverdue(JobApplication a, DateTime today) =>
        a.Deadline.HasValue && a.Status == ApplicationStatus.Saved && a.Deadline.Value.Date < today;

    /// <summary>
    /// Applied, not snoozed (no FollowUpAt later than today in the user's zone), and the later of
    /// DateApplied / LastContactAt (local date) is at least <paramref name="followUpAfterDays"/> days old.
    /// Day N itself counts as due.
    /// </summary>
    public static bool IsFollowUpDue(JobApplication a, UserClock clock, int followUpAfterDays)
    {
        if (a.Status != ApplicationStatus.Applied) return false;
        var today = clock.Today;
        if (a.FollowUpAt.HasValue && clock.ToLocal(a.FollowUpAt.Value).Date > today) return false;
        var anchor = FollowUpAnchor(a, clock);
        if (anchor is null) return false;
        return (today - anchor.Value).Days >= followUpAfterDays;
    }

    public static bool IsFollowUpDue(JobApplication a, DateTime today, int followUpAfterDays) =>
        IsFollowUpDue(a, UserClock.Utc(today), followUpAfterDays);

    /// <summary>Interview falls between today and today + 14 in the user's zone (inclusive, date granularity so a late-evening interview still counts as today's).</summary>
    public static bool IsUpcomingInterview(JobApplication a, UserClock clock)
    {
        if (!a.InterviewAt.HasValue) return false;
        var days = clock.DaysUntil(clock.ToLocal(a.InterviewAt.Value));
        return days >= 0 && days <= InterviewWindowDays;
    }

    public static bool IsUpcomingInterview(JobApplication a, DateTime today) =>
        IsUpcomingInterview(a, UserClock.Utc(today));

    /// <summary>
    /// The date the follow-up clock counts from: the later of DateApplied (a calendar date, as-is)
    /// and LastContactAt (an instant, read in the user's zone), or null.
    /// </summary>
    public static DateTime? FollowUpAnchor(JobApplication a, UserClock clock)
    {
        DateTime? applied = a.DateApplied?.Date, contact = clock.ToLocal(a.LastContactAt)?.Date;
        if (applied is null) return contact;
        if (contact is null) return applied;
        return contact > applied ? contact : applied;
    }

    public static DateTime? FollowUpAnchor(JobApplication a) => FollowUpAnchor(a, UserClock.Utc());

    /// <summary>The day the follow-up became (or becomes) due; used for ordering and the calendar.</summary>
    public static DateTime? FollowUpDueDate(JobApplication a, int followUpAfterDays, UserClock clock) =>
        FollowUpAnchor(a, clock)?.AddDays(followUpAfterDays);

    public static DateTime? FollowUpDueDate(JobApplication a, int followUpAfterDays) =>
        FollowUpDueDate(a, followUpAfterDays, UserClock.Utc());

    // ── Wording shared by every surface ───────────────────

    public static string DeadlineReason(DateTime deadline, DateTime today)
    {
        var days = (deadline.Date - today).Days;
        return days switch
        {
            0    => "Deadline today",
            1    => "Deadline tomorrow",
            > 1  => $"Deadline in {days} days",
            -1   => "Deadline was yesterday",
            _    => $"Deadline was {-days} days ago"
        };
    }

    public static string FollowUpReason(JobApplication a, UserClock clock)
    {
        var anchor = FollowUpAnchor(a, clock);
        if (anchor is null) return "Follow up";
        var days    = (clock.Today - anchor.Value).Days;
        var contact = clock.ToLocal(a.LastContactAt)?.Date;
        var verb    = contact.HasValue && contact.Value >= (a.DateApplied?.Date ?? DateTime.MinValue)
            ? "Last contact"
            : "Applied";
        return $"{verb} {days} day{(days == 1 ? "" : "s")} ago, no reply";
    }

    public static string FollowUpReason(JobApplication a, DateTime today) => FollowUpReason(a, UserClock.Utc(today));

    /// <summary>"today 2:00 PM", "tomorrow 9:30 AM", "Tue 2:00 PM" (within a week), else "Sep 25, 2:00 PM". Both arguments are local (user-zone) values.</summary>
    public static string ShortWhen(DateTime at, DateTime today)
    {
        var inv  = CultureInfo.InvariantCulture;
        var time = at.ToString("h:mm tt", inv);
        var days = (at.Date - today).Days;
        return days switch
        {
            0                => $"today {time}",
            1                => $"tomorrow {time}",
            > 1 and < 7      => $"{at.ToString("ddd", inv)} {time}",
            _                => $"{at.ToString("MMM d", inv)}, {time}"
        };
    }

    /// <summary>Board-card deadline chip: shown for deadlines 0–14 days out; danger ≤3 days, warning ≤7, neutral otherwise.</summary>
    public static (string Text, string Tone)? DeadlineChip(JobApplication a, DateTime today)
    {
        if (!a.Deadline.HasValue) return null;
        var days = (a.Deadline.Value.Date - today).Days;
        if (days < 0 || days > 14) return null;
        var tone = days <= 3 ? "danger" : days <= 7 ? "warning" : "";
        return (DeadlineReason(a.Deadline.Value, today), tone);
    }
}
