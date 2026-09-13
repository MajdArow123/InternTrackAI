using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using InternTrackAI.Data;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Controllers;

/// <summary>
/// iCalendar export. <c>GET /Calendar/feed.ics?token=…</c> is a subscribable feed of every
/// deadline, interview and follow-up for the user whose <c>CalendarToken</c> matches — anonymous
/// on purpose (Google/Apple Calendar fetch it without cookies), so the token is the whole secret
/// and a wrong or missing one is a plain 404. <c>GET /Calendar/application/{id}.ics</c> is the
/// signed-in, owner-scoped single-application download behind the drawer's "Add to calendar".
/// </summary>
public class CalendarController : Controller
{
    /// <summary>Longest token the feed will even look up (real ones are 43 characters).</summary>
    public const int MaxTokenLength = 128;

    private readonly ApplicationDbContext _db;

    public CalendarController(ApplicationDbContext db) => _db = db;

    /// <summary>32 random bytes, Base64Url-encoded (43 URL-safe characters, no padding).</summary>
    public static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    [HttpGet("/Calendar/feed.ics"), AllowAnonymous]
    public async Task<IActionResult> Feed(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength) return NotFound();

        var owner = await _db.UserProfiles.AsNoTracking()
            .Where(p => p.CalendarToken == token)
            .Select(p => p.UserId)
            .FirstOrDefaultAsync();
        if (owner is null) return NotFound();

        var apps = await _db.JobApplications.AsNoTracking().Where(a => a.UserId == owner).ToListAsync();
        var now  = DateTime.UtcNow;
        return Ics(IcsBuilder.Build(apps.SelectMany(a => IcsBuilder.EventsFor(a, now))), "interntrackai.ics");
    }

    [HttpGet("/Calendar/application/{id:int}.ics"), Authorize]
    public async Task<IActionResult> Application(int id)
    {
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var app = await _db.JobApplications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.UserId == uid);
        if (app is null) return NotFound();

        var ics = IcsBuilder.Build(IcsBuilder.EventsFor(app, DateTime.UtcNow), $"{app.CompanyName} — {app.RoleTitle}");
        return Ics(ics, SafeFileName($"{app.CompanyName}-{app.RoleTitle}") + ".ics");
    }

    private FileContentResult Ics(string ics, string fileName)
    {
        Response.Headers.CacheControl = "no-store";
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", fileName);
    }

    private static string SafeFileName(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var name  = new string(chars).Trim('-');
        while (name.Contains("--")) name = name.Replace("--", "-");
        return string.IsNullOrEmpty(name) ? "application" : name;
    }
}
