using Serilog.Events;

namespace InternTrackAI.Services;

/// <summary>
/// What counts as an error in the log. An Error line should mean the server did something wrong: the e2e
/// suite fails a run on any Error line in the server's log (CLAUDE.md §10), and in production real errors
/// have to be findable. A browser that closes a tab or navigates away mid-request is not a server fault, and
/// until 2026-09-26 each one wrote two Error lines — Serilog's request line ("responded 500", because the
/// cancellation surfaced as an exception) and EF Core's "An error occurred using the connection" for the
/// query the cancelled request token stopped. Found by that suite check on the drawer's keyword coverage.
/// </summary>
public static class RequestLogging
{
    /// <summary>The client went away: the request was cancelled because the connection aborted.</summary>
    public static bool IsClientAbort(HttpContext http, Exception? ex) =>
        ex is OperationCanceledException && http.RequestAborted.IsCancellationRequested;

    /// <summary>Level for the one request line <c>UseSerilogRequestLogging</c> writes.</summary>
    public static LogEventLevel LevelFor(HttpContext http, Exception? ex) =>
        IsClientAbort(http, ex) ? LogEventLevel.Information
        : ex != null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
        : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
        : LogEventLevel.Information;

    /// <summary>
    /// EF Core logging a database operation that failed only because it was cancelled. EF is handed the
    /// request's token, so nothing but a client abort cancels it; a real connection or command failure is
    /// never an <see cref="OperationCanceledException"/> and still logs as Error. Excluded rather than
    /// downgraded because Serilog cannot change an event's level, and the request line already records the
    /// abort.
    /// </summary>
    public static bool IsCancelledDatabaseOperation(LogEvent e) =>
        e.Exception is OperationCanceledException
        && e.Properties.TryGetValue("SourceContext", out var source)
        && source is ScalarValue { Value: string context }
        && context.StartsWith("Microsoft.EntityFrameworkCore.Database.", StringComparison.Ordinal);
}
