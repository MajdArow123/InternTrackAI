namespace InternTrackAI.Models;

/// <summary>
/// Backing model for the generic error page. Shows the ASP.NET Core request id (if one
/// was generated) so a user can quote it when reporting an issue.
/// </summary>
public class ErrorViewModel
{
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
