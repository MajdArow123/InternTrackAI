namespace InternTrackAI.Helpers;

/// <summary>Presentation helpers for the profile identity card, shared by the view and the photo endpoints.</summary>
public static class ProfileDisplay
{
    /// <summary>
    /// Up to two initials from the full name ("Alex Johnson" → "AJ"), else the first letter of the
    /// email, else "?". Rendered in the avatar circle whenever there is no photo.
    /// </summary>
    public static string Initials(string? fullName, string? email)
    {
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2);
            return string.Concat(parts.Select(w => char.ToUpperInvariant(w[0])));
        }
        return string.IsNullOrEmpty(email) ? "?" : email.Substring(0, 1).ToUpperInvariant();
    }

    /// <summary>Cache-busted URL of the stored photo, or null when there is none.</summary>
    public static string? PhotoUrl(string? fileName, int version) =>
        fileName is null ? null : $"/uploads/photos/{fileName}?v={version}";
}
