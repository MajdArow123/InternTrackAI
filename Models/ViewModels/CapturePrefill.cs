using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Fields the "Save to InternTrackAI" bookmarklet flow hands to the Create Application form.
/// <c>CaptureController</c> builds one from the analyzer's output (or from just the page URL and
/// title when the analyzer can't read the posting) and redirects to
/// <c>JobApplications/Create</c> with these as query-string parameters; the Create GET binds them
/// back into this type and pre-fills the form. Nothing is saved until the user submits the form.
/// </summary>
public class CapturePrefill
{
    /// <summary>The job posting's public URL — becomes <see cref="JobApplication.JobLink"/>.</summary>
    public string? Url { get; set; }

    /// <summary>The browser tab's title, used as a fallback role title when nothing was extracted.</summary>
    public string? Title { get; set; }

    public string? Company { get; set; }
    public string? Role { get; set; }
    public string? Location { get; set; }
    public string? Salary { get; set; }
    public DateTime? Deadline { get; set; }
    public WorkMode? WorkMode { get; set; }

    /// <summary>True when any field beyond the raw URL/title was supplied.</summary>
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(Url) || !string.IsNullOrWhiteSpace(Title) ||
        !string.IsNullOrWhiteSpace(Company) || !string.IsNullOrWhiteSpace(Role) ||
        !string.IsNullOrWhiteSpace(Location) || !string.IsNullOrWhiteSpace(Salary) ||
        Deadline.HasValue || WorkMode.HasValue;

    /// <summary>Host of <see cref="Url"/> for display ("jobs.lever.co"), or null if it isn't a valid absolute URL.</summary>
    public string? Host =>
        Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : null;

    /// <summary>
    /// Copies the prefill onto a fresh <see cref="JobApplication"/>, trimming each value to the
    /// column's max length so an over-long page title can't trip server-side validation.
    /// When no role was extracted, the page title stands in so the user has something to edit.
    /// </summary>
    public void ApplyTo(JobApplication app)
    {
        app.JobLink     = Truncate(Url, 2048);
        app.CompanyName = Truncate(Company, 100) ?? string.Empty;
        app.RoleTitle   = Truncate(Role, 100) ?? Truncate(Title, 100) ?? string.Empty;
        app.Location    = Truncate(Location, 100);
        app.Salary      = Truncate(Salary, 50);
        app.Deadline    = Deadline;
        if (WorkMode.HasValue) app.WorkMode = WorkMode.Value;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length > max ? value[..max] : value;
    }
}
