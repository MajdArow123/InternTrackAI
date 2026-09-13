namespace InternTrackAI.Services;

/// <summary>
/// Hosted background job that reseeds the demo account once a day at <c>Demo:ResetTimeUtc</c>
/// (default 04:00 UTC). It only arms itself when <c>Demo:AutoReset</c> is true <em>and</em>
/// <c>Demo:Email</c> is configured; otherwise it logs why it is idle and exits. The manual
/// <c>POST /Admin/ResetDemo</c> endpoint shares <see cref="DemoSeeder"/> and works regardless.
/// </summary>
public class DemoResetService : BackgroundService
{
    public static readonly TimeOnly DefaultResetTime = new(4, 0);

    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<DemoResetService> _logger;
    private readonly TimeProvider _clock;

    public DemoResetService(IServiceProvider services, IConfiguration config, ILogger<DemoResetService> logger, TimeProvider? clock = null)
    {
        _services = services;
        _config = config;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when both switches are on: <c>Demo:AutoReset</c> and a configured <c>Demo:Email</c>.</summary>
    public static bool IsEnabled(IConfiguration config) =>
        config.GetValue<bool>("Demo:AutoReset") && !string.IsNullOrWhiteSpace(config["Demo:Email"]);

    /// <summary>Parses <c>Demo:ResetTimeUtc</c> ("HH:mm"), falling back to 04:00.</summary>
    public static TimeOnly ResetTime(IConfiguration config) =>
        TimeOnly.TryParse(config["Demo:ResetTimeUtc"], System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : DefaultResetTime;

    /// <summary>Next UTC instant at <paramref name="time"/> strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset nowUtc, TimeOnly time)
    {
        var today = new DateTimeOffset(nowUtc.UtcDateTime.Date.Add(time.ToTimeSpan()), TimeSpan.Zero);
        return today > nowUtc ? today : today.AddDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var autoReset = _config.GetValue<bool>("Demo:AutoReset");
        var hasEmail  = !string.IsNullOrWhiteSpace(_config["Demo:Email"]);
        var time      = ResetTime(_config);

        if (!IsEnabled(_config))
        {
            var reason = !autoReset ? "Demo:AutoReset is false" : "Demo:Email is not configured";
            _logger.LogInformation("Demo auto-reset is DISABLED ({Reason}). POST /Admin/ResetDemo still works on demand.", reason);
            return;
        }

        _logger.LogInformation("Demo auto-reset is ENABLED: the demo account will be reseeded daily at {Time} UTC.", time.ToString("HH:mm"));

        while (!stoppingToken.IsCancellationRequested)
        {
            var next  = NextRun(_clock.GetUtcNow(), time);
            var delay = next - _clock.GetUtcNow();
            _logger.LogInformation("Next demo reset scheduled for {Next:u} (in {Delay}).", next, delay);

            try
            {
                await Task.Delay(delay, _clock, stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                using var scope = _services.CreateScope();
                var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();
                var result = await seeder.ResetAsync(stoppingToken);
                if (!result.UserFound)
                    _logger.LogWarning("Scheduled demo reset skipped: the configured demo account does not exist.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled demo reset failed; will retry at the next scheduled time.");
            }
        }
    }
}
