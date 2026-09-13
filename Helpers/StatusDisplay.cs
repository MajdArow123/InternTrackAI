using InternTrackAI.Models.Enums;

namespace InternTrackAI.Helpers;

/// <summary>
/// Presentation helpers shared by the Applications, Dashboard, and Profile views: status badge
/// classes and icons, company-avatar initials, and the resume-match tier mapping. Keeping them
/// here means the partials don't each need their own copy of the same switch expressions.
/// </summary>
public static class StatusDisplay
{
    public static string BadgeClass(ApplicationStatus s) => s switch
    {
        ApplicationStatus.Saved     => "badge-saved",
        ApplicationStatus.Applied   => "badge-applied",
        ApplicationStatus.Interview => "badge-interview",
        ApplicationStatus.Offer     => "badge-offer",
        ApplicationStatus.Rejected  => "badge-rejected",
        _                           => ""
    };

    /// <summary>Inline SVG rendered inside a status badge. Emit with <c>Html.Raw</c>.</summary>
    public static string Icon(ApplicationStatus s) => s switch
    {
        ApplicationStatus.Saved     => "<svg class=\"status-icon\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><path d=\"M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z\"/></svg>",
        ApplicationStatus.Applied   => "<svg class=\"status-icon\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><line x1=\"22\" y1=\"2\" x2=\"11\" y2=\"13\"/><polygon points=\"22 2 15 22 11 13 2 9 22 2\"/></svg>",
        ApplicationStatus.Interview => "<svg class=\"status-icon\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><rect x=\"3\" y=\"4\" width=\"18\" height=\"18\" rx=\"2\"/><line x1=\"16\" y1=\"2\" x2=\"16\" y2=\"6\"/><line x1=\"8\" y1=\"2\" x2=\"8\" y2=\"6\"/><line x1=\"3\" y1=\"10\" x2=\"21\" y2=\"10\"/></svg>",
        ApplicationStatus.Offer     => "<svg class=\"status-icon\" viewBox=\"0 0 24 24\" fill=\"currentColor\" stroke=\"none\"><polygon points=\"12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2\"/></svg>",
        ApplicationStatus.Rejected  => "<svg class=\"status-icon\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><circle cx=\"12\" cy=\"12\" r=\"10\"/><line x1=\"15\" y1=\"9\" x2=\"9\" y2=\"15\"/><line x1=\"9\" y1=\"9\" x2=\"15\" y2=\"15\"/></svg>",
        _                           => ""
    };

    /// <summary>1-2 letter avatar text from a company name ("Shopify Inc" → "SI", "Shopify" → "SH").</summary>
    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $"{parts[0][0]}{parts[1][0]}".ToUpper()
            : name.Length >= 2 ? name[..2].ToUpper() : name.ToUpper();
    }

    /// <summary>5-tier match band (80/60/40/20). Mirrored by the TIERS table in the front-end JS.</summary>
    public static int MatchTier(int score) => score switch
    {
        >= 80 => 5,
        >= 60 => 4,
        >= 40 => 3,
        >= 20 => 2,
        _     => 1
    };

    public static string MatchTooltip(int score) => score switch
    {
        >= 75 => "Apply",
        >= 60 => "Review",
        >= 45 => "Consider",
        _     => "Skip"
    };
}
