using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Services;

/// <summary>
/// Settings for the per-user rate limit applied to every endpoint that calls OpenAI.
/// Bound from the <c>RateLimiting:AI</c> configuration section (environment variables
/// <c>RateLimiting__AI__PermitLimit</c> etc. on Railway).
/// </summary>
public class AiRateLimitOptions
{
    public const string SectionName = "RateLimiting:AI";

    /// <summary>Requests allowed per user per window across all AI endpoints combined.</summary>
    public int PermitLimit { get; set; } = 20;

    /// <summary>Length of the fixed window in minutes.</summary>
    public int WindowMinutes { get; set; } = 60;

    /// <summary>Tighter allowance for the shared demo account (matched on <c>Demo:Email</c>).</summary>
    public int DemoPermitLimit { get; set; } = 10;
}

/// <summary>
/// Marks an "ai"-limited action whose demo-account branch answers with a canned or refused response and never calls
/// OpenAI (e.g. the follow-up draft). For the demo account the policy then takes no permit, so browsing those
/// features doesn't use up the demo's AI allowance. Everyone else is limited as usual. Only put it on actions that
/// check <see cref="ConfiguredAccounts.IsDemoUser"/> before any model call.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class NoAiCallForDemoAttribute : Attribute { }

/// <summary>
/// The per-user AI bucket itself, shared by the HTTP policy below and by code that calls OpenAI
/// outside a request (the Gmail sync). One fixed-window limiter per user id; a demo user gets the
/// tighter <see cref="AiRateLimitOptions.DemoPermitLimit"/>. Registered as a singleton.
/// </summary>
public sealed class AiUsageLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<(string Key, int Limit)> _limiter;
    private readonly IOptionsMonitor<AiRateLimitOptions> _options;

    public AiUsageLimiter(IOptionsMonitor<AiRateLimitOptions> options)
    {
        _options = options;
        _limiter = PartitionedRateLimiter.Create<(string Key, int Limit), string>(resource =>
            RateLimitPartition.GetFixedWindowLimiter(resource.Key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit       = Math.Max(1, resource.Limit),
                Window            = TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.WindowMinutes)),
                QueueLimit        = 0,
                AutoReplenishment = true
            }));
    }

    public int LimitFor(bool isDemo) => isDemo ? _options.CurrentValue.DemoPermitLimit : _options.CurrentValue.PermitLimit;

    public int WindowMinutes => _options.CurrentValue.WindowMinutes;

    /// <summary>Takes one permit from the user's bucket; check <see cref="RateLimitLease.IsAcquired"/>.</summary>
    public RateLimitLease TryAcquire(string userKey, bool isDemo) => _limiter.AttemptAcquire((userKey, LimitFor(isDemo)), 1);

    /// <summary>A <see cref="RateLimiter"/> view over one user's bucket for the ASP.NET policy (disposal there never disposes the bucket).</summary>
    public RateLimiter ForUser(string userKey, bool isDemo) => new PartitionView(_limiter, (userKey, LimitFor(isDemo)));

    public void Dispose() => _limiter.Dispose();

    private sealed class PartitionView : RateLimiter
    {
        private readonly PartitionedRateLimiter<(string Key, int Limit)> _owner;
        private readonly (string Key, int Limit) _resource;

        public PartitionView(PartitionedRateLimiter<(string Key, int Limit)> owner, (string Key, int Limit) resource)
        {
            _owner = owner;
            _resource = resource;
        }

        // Never reported idle, so the framework never disposes and recreates the view mid-window.
        public override TimeSpan? IdleDuration => null;
        public override RateLimiterStatistics? GetStatistics() => _owner.GetStatistics(_resource);
        protected override RateLimitLease AttemptAcquireCore(int permitCount) => _owner.AttemptAcquire(_resource, permitCount);
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) => _owner.AcquireAsync(_resource, permitCount, cancellationToken);
    }
}

/// <summary>
/// The wall-clock cap on one OpenAI call, and the extension every AI <c>HttpClient</c> registration
/// carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this an AI client inherits <see cref="HttpClient"/>'s 100-second default</b>, which is
/// not a timeout anyone chose: a hung connection holds the request — and the user's spinner — for the
/// better part of two minutes, and it is the same order as Railway's own
/// <c>healthcheckTimeout</c>. Every other outbound client in this app already caps itself
/// (<c>ResendEmailSender</c> and <c>GitHubService</c> at 10s, <c>UrlFetcher</c> at 15s); the AI ones
/// were the gap.
/// </para>
/// <para>
/// 30 seconds is the spec's number and is comfortably above what these calls take —
/// <c>max_tokens</c> is 200–2000 across the services, so a normal reply lands in a few seconds. A
/// generation that has not answered in 30s is not going to produce something the user still wants.
/// <c>CaptureController</c> caps its own analyzer call tighter (<c>Capture:AnalyzeTimeoutSeconds</c>,
/// default 15) because a bookmarklet round-trip has a person waiting on a redirect.
/// </para>
/// </remarks>
public static class AiHttpDefaults
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Applies <see cref="Timeout"/>. Every AI client registration in Program.cs ends with this.</summary>
    public static IHttpClientBuilder AiTimeout(this IHttpClientBuilder builder) =>
        builder.ConfigureHttpClient(c => c.Timeout = Timeout);
}

/// <summary>
/// Wires ASP.NET Core's built-in rate limiter with a single named policy, <see cref="PolicyName"/>,
/// partitioned by the signed-in user's id (falling back to the client IP for anonymous callers,
/// which the [Authorize] filters normally turn away first). The partitions are views over
/// <see cref="AiUsageLimiter"/>, so background AI calls for the same user draw from the same bucket.
/// Rejections are answered with a friendly payload instead of a bare 429: JSON
/// <c>{ success:false, error }</c> for fetch() callers, or a redirect back to the referring page
/// with an error toast for plain form posts.
/// </summary>
public static class AiRateLimiting
{
    public const string PolicyName = "ai";

    /// <summary>Partition for demo requests to <see cref="NoAiCallForDemoAttribute"/> actions; distinct from every user-id key so it never shares a cached limiter.</summary>
    private const string DemoCannedPartition = "demo-canned";

    public static IServiceCollection AddAiRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AiRateLimitOptions>(config.GetSection(AiRateLimitOptions.SectionName));
        services.AddSingleton<AiUsageLimiter>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PolicyName, httpContext =>
            {
                var limiter = httpContext.RequestServices.GetRequiredService<AiUsageLimiter>();
                var userId  = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
                var key     = userId ?? ("ip:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
                var isDemo  = IsDemoUser(httpContext, config);

                // Endpoint routing has already run (UseRouting precedes UseRateLimiter), so the action's metadata is here.
                if (isDemo && httpContext.GetEndpoint()?.Metadata.GetMetadata<NoAiCallForDemoAttribute>() is not null)
                    return RateLimitPartition.GetNoLimiter(DemoCannedPartition);

                return RateLimitPartition.Get(key, _ => limiter.ForUser(key, isDemo));
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                var opts = http.RequestServices.GetRequiredService<IOptions<AiRateLimitOptions>>().Value;
                var limit = IsDemoUser(http, config) ? opts.DemoPermitLimit : opts.PermitLimit;

                TimeSpan? retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? ra : null;
                if (retryAfter.HasValue)
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.Value.TotalSeconds)).ToString();

                var message = BuildMessage(limit, opts.WindowMinutes, retryAfter);
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (WantsJson(http.Request))
                {
                    // hasResume:true keeps the Create page's auto-match card from misreading the
                    // rejection as "no resume uploaded"; every caller then falls through to `error`.
                    await http.Response.WriteAsJsonAsync(new
                    {
                        success     = false,
                        hasResume   = true,
                        rateLimited = true,
                        error       = message
                    }, cancellationToken);
                    return;
                }

                // Plain form post (e.g. Profile → Score my resume): bounce back with a toast.
                var tempDataFactory = http.RequestServices.GetRequiredService<ITempDataDictionaryFactory>();
                var tempData = tempDataFactory.GetTempData(http);
                tempData["Toast"] = "error|" + message;
                tempData.Save();

                var referer = http.Request.Headers.Referer.ToString();
                var target  = IsLocalUrl(http, referer) ? referer : "/";
                http.Response.Redirect(target);
            };
        });

        return services;
    }

    /// <summary>Human-readable rejection text, e.g. "You've reached the limit of 20 AI requests per hour. Try again in about 12 minutes."</summary>
    public static string BuildMessage(int limit, int windowMinutes, TimeSpan? retryAfter)
    {
        var window = windowMinutes == 60 ? "hour"
                   : windowMinutes % 60 == 0 ? $"{windowMinutes / 60} hours"
                   : $"{windowMinutes} minutes";
        var msg = $"You've reached the limit of {limit} AI requests per {window}.";
        if (retryAfter.HasValue)
        {
            var mins = Math.Max(1, (int)Math.Ceiling(retryAfter.Value.TotalMinutes));
            msg += mins == 1 ? " Try again in about a minute." : $" Try again in about {mins} minutes.";
        }
        return msg;
    }

    private static bool IsDemoUser(HttpContext http, IConfiguration config) => IsDemoUser(http.User, config);

    /// <summary>True when the signed-in principal is the shared demo account (email claim, else the Identity user name).</summary>
    public static bool IsDemoUser(ClaimsPrincipal user, IConfiguration config) => ConfiguredAccounts.IsDemoUser(user, config);

    /// <summary>True when <paramref name="email"/> is the configured shared demo account (case- and whitespace-insensitive).</summary>
    public static bool IsDemoEmail(string? email, IConfiguration config) =>
        ConfiguredAccounts.IsConfigured(email, config, ConfiguredAccounts.DemoEmailKey);

    private static bool WantsJson(HttpRequest request)
    {
        if (request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (request.Headers.XRequestedWith == "XMLHttpRequest") return true;
        return request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalUrl(HttpContext http, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith('/') && !url.StartsWith("//")) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var abs)
            && string.Equals(abs.Host, http.Request.Host.Host, StringComparison.OrdinalIgnoreCase);
    }
}
