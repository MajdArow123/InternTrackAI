using InternTrackAI.Services;
using Microsoft.AspNetCore.Http;
using Serilog.Events;
using Serilog.Parsing;

namespace InternTrackAI.Tests;

/// <summary>
/// A client abort is not a server error; everything else that was an error still is. Both halves matter:
/// the e2e suite fails a run on any Error line (so noise would make it useless), and a real failure must
/// never be reclassified into silence.
/// </summary>
public class RequestLoggingTests
{
    private static HttpContext Context(string path = "/JobApplications/KeywordCoverage", bool aborted = false, int status = 200)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Response.StatusCode = status;
        if (aborted) { var cts = new CancellationTokenSource(); cts.Cancel(); http.RequestAborted = cts.Token; }
        return http;
    }

    [Fact]
    public void A_request_the_client_aborted_logs_at_information() =>
        Assert.Equal(LogEventLevel.Information, RequestLogging.LevelFor(Context(aborted: true), new TaskCanceledException()));

    [Fact]
    public void A_cancellation_the_client_did_not_cause_is_still_an_error() =>
        // e.g. a timeout inside the app: the request is still live, so this is the server's problem.
        Assert.Equal(LogEventLevel.Error, RequestLogging.LevelFor(Context(aborted: false), new TaskCanceledException()));

    [Fact]
    public void Any_other_exception_is_an_error_even_if_the_client_has_gone() =>
        Assert.Equal(LogEventLevel.Error, RequestLogging.LevelFor(Context(aborted: true), new InvalidOperationException()));

    [Fact]
    public void A_500_without_an_exception_is_an_error() =>
        Assert.Equal(LogEventLevel.Error, RequestLogging.LevelFor(Context(status: 500), null));

    [Fact]
    public void Health_checks_stay_verbose() =>
        Assert.Equal(LogEventLevel.Verbose, RequestLogging.LevelFor(Context("/health"), null));

    private static LogEvent Event(string source, Exception ex) => new(
        DateTimeOffset.UtcNow, LogEventLevel.Error, ex, new MessageTemplateParser().Parse("x"),
        new[] { new LogEventProperty("SourceContext", new ScalarValue(source)) });

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Connection")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    public void A_database_operation_stopped_by_cancellation_is_dropped(string source) =>
        Assert.True(RequestLogging.IsCancelledDatabaseOperation(Event(source, new TaskCanceledException())));

    [Fact]
    public void A_real_database_failure_is_kept() =>
        Assert.False(RequestLogging.IsCancelledDatabaseOperation(Event("Microsoft.EntityFrameworkCore.Database.Connection", new InvalidOperationException("connection refused"))));

    [Fact]
    public void A_cancellation_logged_by_anything_but_EF_is_kept() =>
        Assert.False(RequestLogging.IsCancelledDatabaseOperation(Event("InternTrackAI.Services.KeywordCoverageService", new TaskCanceledException())));
}
