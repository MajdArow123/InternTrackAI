using InternTrackAI.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Services.Gmail;

/// <summary>
/// Runs <see cref="GmailSyncService.SyncAsync"/> for every connected account every
/// <see cref="GmailOptions.SyncIntervalMinutes"/> minutes (default 30). Each user gets a fresh DI
/// scope and a try/catch, so one expired token never stops the others. Exits immediately, with a log
/// line saying why, when Google OAuth is not configured. Mirrors <see cref="DemoResetService"/>.
/// </summary>
public class GmailSyncHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly IOptionsMonitor<GmailOptions> _options;
    private readonly ILogger<GmailSyncHostedService> _logger;
    private readonly TimeProvider _clock;

    public GmailSyncHostedService(IServiceProvider services, IConfiguration config, IOptionsMonitor<GmailOptions> options,
                                  ILogger<GmailSyncHostedService> logger, TimeProvider? clock = null)
    {
        _services = services;
        _config = config;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public static TimeSpan Interval(GmailOptions o) => TimeSpan.FromMinutes(Math.Clamp(o.SyncIntervalMinutes, 1, 24 * 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!GmailIntegration.IsConfigured(_config))
        {
            _logger.LogInformation("Gmail sync is DISABLED (Google:ClientId / Google:ClientSecret not configured).");
            return;
        }

        var interval = Interval(_options.CurrentValue);
        _logger.LogInformation("Gmail sync is ENABLED: connected accounts are synced every {Minutes} minutes.", interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, _clock, stoppingToken); }
            catch (OperationCanceledException) { break; }

            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>One pass over every connection. Public so a test (or an operator hook) can drive it without waiting.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        List<string> userIds;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userIds = await db.GmailConnections.AsNoTracking().Select(c => c.UserId).ToListAsync(ct);
        }

        int ok = 0, failed = 0, suggestions = 0;
        foreach (var userId in userIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var scope = _services.CreateScope();
                var sync   = scope.ServiceProvider.GetRequiredService<GmailSyncService>();
                var result = await sync.SyncAsync(userId, ct);
                if (result.Success) { ok++; suggestions += result.Suggestions; }
                else { failed++; _logger.LogWarning("Scheduled Gmail sync for user {UserId} did not complete: {Error}", userId, result.Error ?? "not connected"); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Scheduled Gmail sync failed for user {UserId}; continuing with the next account.", userId);
            }
        }

        if (userIds.Count > 0)
            _logger.LogInformation("Scheduled Gmail sync finished: {Ok} accounts synced, {Failed} failed, {Suggestions} new suggestions.", ok, failed, suggestions);
    }
}
