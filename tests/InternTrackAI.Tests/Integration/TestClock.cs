using InternTrackAI.Services;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// "Today" for a test user, in the zone a test user actually gets.
///
/// Integration tests can't pin the clock the way the unit tests do — the app resolves its own
/// <see cref="UserClock"/> per request — so seeded dates have to be built against the same zone the code
/// will read them back in. A registered user has no <c>TimeZoneId</c> yet, so they get
/// <see cref="TimeZones.DefaultZoneId"/> (America/Toronto), and every "N days ago" rule in
/// <see cref="ReminderService"/> counts from that zone's date.
///
/// Seeding from <c>DateTime.UtcNow.Date</c> instead is wrong for the hours when UTC has rolled into
/// tomorrow and the user's zone has not — after 20:00 in Toronto, eight hours a day in summer. The
/// arithmetic silently shifts by one, so "applied 9 days ago" renders as "8 days ago" and a date sitting
/// on a window boundary (deadline in exactly 7 days, interview in exactly 14) drops out of the list
/// altogether. <see cref="ReminderService"/> itself is consistent about this — pinned by
/// <c>ReminderServiceTests.Every_rule_and_its_wording_is_identical_morning_and_evening</c> — so when a test
/// like that fails only in the evening, it is the seed that is wrong, not the rule.
///
/// Use <see cref="Today"/> for calendar dates (Deadline, DateApplied) and <see cref="Instant"/> for the
/// UTC instants (InterviewAt, FollowUpAt, LastContactAt) that a wall-clock time has to survive as.
/// </summary>
internal static class TestClock
{
    /// <summary>The clock a freshly registered test user gets: the default zone, running now.</summary>
    public static UserClock User => UserClock.For(TimeZones.DefaultZoneId);

    /// <summary>Today's calendar date in the test user's zone.</summary>
    public static DateTime Today => User.Today;

    /// <summary>
    /// A UTC instant for a wall-clock time on one of the user's local days, e.g.
    /// <c>Instant(Today.AddDays(2), 14)</c> for "2pm the day after tomorrow, their time".
    /// Storing the local value directly would drift the hour and can move the date across midnight.
    /// </summary>
    public static DateTime Instant(DateTime localDate, int hour = 9) => User.ToUtc(localDate.Date.AddHours(hour));
}
