using System.Globalization;
using System.Security.Claims;
using InternTrackAI.Data;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>
/// The one place that moves between UTC (storage) and a user's wall-clock time (screen and forms).
/// Immutable and pure — built from a zone and a "now", so rules and tests can pin the clock. Views
/// and controllers get one from <see cref="UserClockProvider"/> and never call TimeZoneInfo or
/// DateTime.Now themselves.
///
/// Two kinds of value flow through here and they are deliberately handled differently:
/// <list type="bullet">
/// <item><b>Instants</b> (InterviewAt, FollowUpAt, LastContactAt, note CreatedAt, upload/generation
/// stamps) are stored in UTC and shifted with <see cref="ToLocal(DateTime)"/> / <see cref="ToUtc(DateTime)"/>.</item>
/// <item><b>Calendar dates</b> (Deadline, DateApplied) have no time of day and no zone: a deadline
/// of Oct 1 is Oct 1 everywhere. They are formatted with <see cref="Date(DateTime?, string)"/> as-is;
/// only the "today" they are compared against comes from the user's zone (<see cref="Today"/>).</item>
/// </list>
/// </summary>
public sealed class UserClock
{
    public const string DateFormat     = "MMM d, yyyy";
    public const string DateTimeFormat = "MMM d, yyyy 'at' h:mm tt";
    public const string TimeFormat     = "h:mm tt";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public TimeZoneInfo Zone   { get; }
    public DateTime     NowUtc { get; }

    public UserClock(TimeZoneInfo zone, DateTime? nowUtc = null)
    {
        Zone   = zone;
        NowUtc = DateTime.SpecifyKind(nowUtc ?? DateTime.UtcNow, DateTimeKind.Utc);
    }

    /// <summary>A clock in the given IANA zone (unknown ids fall back to <see cref="TimeZones.DefaultZoneId"/>).</summary>
    public static UserClock For(string? zoneId, DateTime? nowUtc = null) => new(TimeZones.Resolve(zoneId), nowUtc);

    /// <summary>A clock that shifts nothing — what every date used to be before per-user zones existed.</summary>
    public static UserClock Utc(DateTime? nowUtc = null) => new(TimeZoneInfo.Utc, nowUtc);

    /// <summary>The IANA id as stored ("America/Toronto"); "UTC" for the UTC clock.</summary>
    public string ZoneId => Zone.Id == TimeZoneInfo.Utc.Id || Zone.Id == "Etc/UTC" ? "UTC" : Zone.Id;

    public bool IsUtc => Zone.BaseUtcOffset == TimeSpan.Zero && !Zone.SupportsDaylightSavingTime;

    /// <summary>Wall-clock now in the user's zone (Kind = Unspecified).</summary>
    public DateTime NowLocal => ToLocal(NowUtc);

    /// <summary>Today's date in the user's zone — the anchor for every "in N days" / "N days ago".</summary>
    public DateTime Today => NowLocal.Date;

    // ── Conversion ────────────────────────────────────────

    /// <summary>UTC instant → the user's wall-clock time (Kind = Unspecified, so it can't be re-shifted by accident).</summary>
    public DateTime ToLocal(DateTime utc)
    {
        var instant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(instant, Zone), DateTimeKind.Unspecified);
    }

    public DateTime? ToLocal(DateTime? utc) => utc.HasValue ? ToLocal(utc.Value) : null;

    /// <summary>
    /// The user's wall-clock time (as typed into a form) → UTC instant. A time that doesn't exist
    /// because the clocks jumped forward (2:30 AM on the spring-forward night) is moved an hour later;
    /// an ambiguous time on the fall-back night resolves to standard time, the same way calendars do.
    /// </summary>
    public DateTime ToUtc(DateTime local)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(wall, Zone);
    }

    public DateTime? ToUtc(DateTime? local) => local.HasValue ? ToUtc(local.Value) : null;

    /// <summary>Midnight (start of day) of a local calendar date, as a UTC instant.</summary>
    public DateTime StartOfLocalDayUtc(DateTime localDate) => ToUtc(localDate.Date);

    /// <summary>Whole days from <see cref="Today"/> to a calendar date: 0 today, 1 tomorrow, −1 yesterday.</summary>
    public int DaysUntil(DateTime calendarDate) => (calendarDate.Date - Today).Days;

    // ── Formatting (all InvariantCulture, US-style as the rest of the app) ──

    /// <summary>A calendar date (Deadline, DateApplied) exactly as stored: "Oct 1, 2026".</summary>
    public string Date(DateTime? calendarDate, string empty = "") =>
        calendarDate.HasValue ? calendarDate.Value.ToString(DateFormat, Inv) : empty;

    /// <summary>A UTC instant as the user's local date: "Sep 20, 2026".</summary>
    public string LocalDate(DateTime? utc, string empty = "") =>
        utc.HasValue ? ToLocal(utc.Value).ToString(DateFormat, Inv) : empty;

    /// <summary>A UTC instant as the user's local date and time: "Sep 20, 2026 at 2:00 PM".</summary>
    public string LocalDateTime(DateTime? utc, string empty = "") =>
        utc.HasValue ? ToLocal(utc.Value).ToString(DateTimeFormat, Inv) : empty;

    /// <summary>A UTC instant as the user's local time of day: "2:00 PM".</summary>
    public string LocalTime(DateTime? utc, string empty = "") =>
        utc.HasValue ? ToLocal(utc.Value).ToString(TimeFormat, Inv) : empty;

    /// <summary>Value for an <c>&lt;input type="datetime-local"&gt;</c>: "2026-09-20T14:00" in the user's zone.</summary>
    public string InputDateTime(DateTime? utc) =>
        utc.HasValue ? ToLocal(utc.Value).ToString("yyyy-MM-dd'T'HH:mm", Inv) : "";

    /// <summary>Value for an <c>&lt;input type="date"&gt;</c> holding an instant: "2026-09-20" in the user's zone.</summary>
    public string InputDate(DateTime? utc) =>
        utc.HasValue ? ToLocal(utc.Value).ToString("yyyy-MM-dd", Inv) : "";
}

/// <summary>
/// Hands out the <see cref="UserClock"/> for the signed-in user (one profile lookup per request,
/// cached) or for an arbitrary user id (the anonymous calendar feed, which knows only the owner).
/// Anonymous requests and users without a profile row get the default zone.
/// </summary>
public class UserClockProvider
{
    private readonly ApplicationDbContext _db;
    private readonly IHttpContextAccessor _http;
    private Task<UserClock>? _current;

    public UserClockProvider(ApplicationDbContext db, IHttpContextAccessor http)
    {
        _db   = db;
        _http = http;
    }

    /// <summary>The current request's user clock; the default zone when nobody is signed in.</summary>
    public Task<UserClock> GetAsync() => _current ??= LoadCurrentAsync();

    /// <summary>The clock for a specific user (the calendar feed resolves its owner from the token).</summary>
    public async Task<UserClock> ForUserAsync(string userId)
    {
        var zoneId = await _db.UserProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.TimeZoneId)
            .FirstOrDefaultAsync();
        return UserClock.For(zoneId);
    }

    private Task<UserClock> LoadCurrentAsync()
    {
        var uid = _http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return uid is null ? Task.FromResult(UserClock.For(TimeZones.DefaultZoneId)) : ForUserAsync(uid);
    }
}
