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

    /// <summary>Requests allowed per user per window across every AI endpoint except practice.</summary>
    public int PermitLimit { get; set; } = 20;

    /// <summary>Length of the fixed window in minutes.</summary>
    public int WindowMinutes { get; set; } = 60;

    /// <summary>Tighter allowance for the shared demo account (matched on <c>Demo:Email</c>).</summary>
    public int DemoPermitLimit { get; set; } = 10;

    /// <summary>The practice bucket. Both buckets live here so there is one place to read when debugging a 429.</summary>
    public PracticeRateLimitOptions Practice { get; set; } = new();

    /// <summary>The limit and window that apply to one caller, given which bucket they are spending from.</summary>
    public AiBucketLimits For(AiBucket bucket, bool isDemo) => bucket switch
    {
        AiBucket.Practice => isDemo
            ? new AiBucketLimits(Practice.DemoPermitLimit, Practice.DemoWindowMinutes)
            : new AiBucketLimits(Practice.PermitLimit, Practice.WindowMinutes),
        // The default bucket has no separate demo window: demo and signed-in share WindowMinutes.
        _ => new AiBucketLimits(isDemo ? DemoPermitLimit : PermitLimit, WindowMinutes)
    };
}

/// <summary>
/// The practice bucket: question generation and answer scoring, kept apart from every other AI path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is separate.</b> The two workloads have opposite shapes. Practice is inherently
/// many-small-calls — one round is 1 generate plus 5 scores — while resume parsing is rare and
/// expensive. Sharing one bucket meant a demo visitor could not finish a single round before
/// exhausting the allowance for the whole app, and the 429 read as "the site is locked" when cover
/// letters and resume tools were still fine.
/// </para>
/// <para>
/// <b>"Day" here means 24 hours from this user's first practice call</b> — see the remarks on
/// <see cref="AiUsageLimiter"/>. It is not calendar-aligned, and it is not durable across a deploy.
/// </para>
/// </remarks>
public class PracticeRateLimitOptions
{
    /// <summary>Signed-in allowance. Generous because the batch button makes many-small-calls normal.</summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>A rolling 24 hours, anchored at first use. Not midnight.</summary>
    public int WindowMinutes { get; set; } = 1440;

    /// <summary>Enough for roughly three full rounds, which is what actually demonstrates the feature.</summary>
    public int DemoPermitLimit { get; set; } = 25;

    /// <summary>
    /// The demo stays hourly while signed-in users are daily, which is the only reason this field
    /// exists: within one bucket the <em>window</em> differs by account type, not just the count.
    /// </summary>
    public int DemoWindowMinutes { get; set; } = 60;
}

/// <summary>Which allowance a call spends from.</summary>
public enum AiBucket
{
    /// <summary>Everything except practice: analysis, matching, cover letters, resume parsing, Gmail.</summary>
    Default = 0,

    /// <summary>Practice question generation and answer scoring.</summary>
    Practice = 1
}

/// <summary>One bucket's resolved numbers for one caller.</summary>
public readonly record struct AiBucketLimits(int PermitLimit, int WindowMinutes);

/// <summary>
/// Marks an "ai"-limited action whose demo-account branch answers with a canned or refused response and never calls
/// OpenAI (e.g. the follow-up draft). For the demo account the policy then takes no permit, so browsing those
/// features doesn't use up the demo's AI allowance. Everyone else is limited as usual. Only put it on actions that
/// check <see cref="ConfiguredAccounts.IsDemoUser"/> before any model call.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class NoAiCallForDemoAttribute : Attribute { }

/// <summary>
/// The per-user AI buckets, shared by the HTTP policies below and by code that calls OpenAI outside a
/// request (the Gmail sync, resume-upload auto-parse). Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two buckets, keyed apart.</b> <see cref="AiBucket.Practice"/> and
/// <see cref="AiBucket.Default"/> are separate allowances so practice — which is many small calls —
/// cannot exhaust resume parsing, which is rare and expensive. The partition is keyed on the string
/// from <c>KeyFor</c> <em>alone</em>, so the two must never produce the same key; a shared key would
/// silently reuse whichever limiter was created first, limit and all.
/// </para>
/// <para>
/// <b>"Per day" means 24 hours from first use, not midnight.</b> The signed-in practice allowance is
/// a 1440-minute fixed window, and .NET's fixed-window limiter counts from the moment a partition is
/// created — the caller's first practice call. So one user's day starts at 09:00 and another's at
/// 21:00, and neither aligns to a calendar date or to an OpenAI billing day.
/// <c>FixedWindowRateLimiterOptions</c> has no anchor, and since these buckets are in-memory and
/// already reset on every deploy, a calendar-aligned window would look exact while being no more
/// durable. Honest rolling was chosen over false precision.
/// </para>
/// <para>
/// <b>Limits and windows are captured when a partition is first created and never re-read.</b> The
/// factory runs once per key, so changing <c>RateLimiting:AI:*</c> at runtime affects only partitions
/// that do not exist yet. In practice: a config change needs a restart.
/// </para>
/// </remarks>
public sealed class AiUsageLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Resource> _limiter;
    private readonly IOptionsMonitor<AiRateLimitOptions> _options;

    /// <summary>
    /// One bucket for one caller. <see cref="Key"/> alone decides the partition; the other two are
    /// read once, when that partition is first created.
    /// </summary>
    private readonly record struct Resource(string Key, int Limit, int WindowMinutes);

    public AiUsageLimiter(IOptionsMonitor<AiRateLimitOptions> options)
    {
        _options = options;
        _limiter = PartitionedRateLimiter.Create<Resource, string>(resource =>
            RateLimitPartition.GetFixedWindowLimiter(resource.Key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit       = Math.Max(1, resource.Limit),
                Window            = TimeSpan.FromMinutes(Math.Max(1, resource.WindowMinutes)),
                QueueLimit        = 0,
                AutoReplenishment = true
            }));
    }

    /// <summary>
    /// The partition key. <b>The two buckets must never share one</b>: the partition is keyed on this
    /// string alone, so the same key with a different limit silently reuses the first limiter.
    /// </summary>
    private static string KeyFor(AiBucket bucket, string userKey) =>
        bucket == AiBucket.Practice ? "practice:" + userKey : userKey;

    public AiBucketLimits LimitsFor(AiBucket bucket, bool isDemo) => _options.CurrentValue.For(bucket, isDemo);

    public int LimitFor(bool isDemo) => LimitsFor(AiBucket.Default, isDemo).PermitLimit;

    public int WindowMinutes => _options.CurrentValue.WindowMinutes;

    /// <summary>Takes one permit from the caller's bucket; check <see cref="RateLimitLease.IsAcquired"/>.</summary>
    public RateLimitLease TryAcquire(string userKey, bool isDemo, AiBucket bucket = AiBucket.Default)
    {
        var limits = LimitsFor(bucket, isDemo);
        return _limiter.AttemptAcquire(new Resource(KeyFor(bucket, userKey), limits.PermitLimit, limits.WindowMinutes), 1);
    }

    /// <summary>A <see cref="RateLimiter"/> view over one bucket for the ASP.NET policy (disposal there never disposes the bucket).</summary>
    public RateLimiter ForUser(string userKey, bool isDemo, AiBucket bucket = AiBucket.Default)
    {
        var limits = LimitsFor(bucket, isDemo);
        return new PartitionView(_limiter, new Resource(KeyFor(bucket, userKey), limits.PermitLimit, limits.WindowMinutes));
    }

    public void Dispose() => _limiter.Dispose();

    private sealed class PartitionView : RateLimiter
    {
        private readonly PartitionedRateLimiter<Resource> _owner;
        private readonly Resource _resource;

        public PartitionView(PartitionedRateLimiter<Resource> owner, Resource resource)
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

    /// <summary>
    /// The practice allowance: question generation and answer scoring. Separate from
    /// <see cref="PolicyName"/> so a visitor can finish a round without spending the resume tools'
    /// budget — see <see cref="PracticeRateLimitOptions"/>.
    /// </summary>
    public const string PracticePolicyName = "ai-practice";

    /// <summary>Partition for demo requests to <see cref="NoAiCallForDemoAttribute"/> actions; distinct from every user-id key so it never shares a cached limiter.</summary>
    private const string DemoCannedPartition = "demo-canned";

    public static IServiceCollection AddAiRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AiRateLimitOptions>(config.GetSection(AiRateLimitOptions.SectionName));
        services.AddSingleton<AiUsageLimiter>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Same body, different bucket. Both partition on the caller; AiUsageLimiter.KeyFor keeps
            // the two key spaces apart.
            options.AddPolicy(PolicyName, http => Partition(http, config, AiBucket.Default));
            options.AddPolicy(PracticePolicyName, http => Partition(http, config, AiBucket.Practice));

            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                var opts = http.RequestServices.GetRequiredService<IOptions<AiRateLimitOptions>>().Value;
                var bucket = BucketOf(http);
                var limits = opts.For(bucket, IsDemoUser(http, config));

                TimeSpan? retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? ra : null;
                if (retryAfter.HasValue)
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.Value.TotalSeconds)).ToString();

                var message = BuildMessage(limits.PermitLimit, limits.WindowMinutes, retryAfter, bucket);
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
    /// <summary>The partition for one request, in one bucket.</summary>
    private static RateLimitPartition<string> Partition(HttpContext httpContext, IConfiguration config, AiBucket bucket)
    {
        var limiter = httpContext.RequestServices.GetRequiredService<AiUsageLimiter>();
        var userId  = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var caller  = userId ?? ("ip:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
        var isDemo  = IsDemoUser(httpContext, config);

        // Endpoint routing has already run (UseRouting precedes UseRateLimiter), so the action's metadata is here.
        if (isDemo && httpContext.GetEndpoint()?.Metadata.GetMetadata<NoAiCallForDemoAttribute>() is not null)
            return RateLimitPartition.GetNoLimiter(DemoCannedPartition);

        // The partition key must match the one AiUsageLimiter uses internally, or the HTTP policy and a
        // manual TryAcquire would spend from two different buckets for the same user.
        var key = bucket == AiBucket.Practice ? "practice:" + caller : caller;
        return RateLimitPartition.Get(key, _ => limiter.ForUser(caller, isDemo, bucket));
    }

    /// <summary>Which bucket the rejected endpoint was spending from, read back off its own attribute.</summary>
    private static AiBucket BucketOf(HttpContext http) =>
        http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == PracticePolicyName
            ? AiBucket.Practice
            : AiBucket.Default;

    /// <summary>
    /// The user-facing 429 text. <paramref name="bucket"/> is what stops it reading as "the whole app
    /// is locked" when only one allowance is gone: a practice rejection says so, and says what still
    /// works.
    /// </summary>
    public static string BuildMessage(int limit, int windowMinutes, TimeSpan? retryAfter, AiBucket bucket = AiBucket.Default)
    {
        var window = windowMinutes == 60 ? "hour"
                   : windowMinutes == 1440 ? "day"
                   : windowMinutes % 60 == 0 ? $"{windowMinutes / 60} hours"
                   : $"{windowMinutes} minutes";

        var msg = bucket == AiBucket.Practice
            ? $"You've reached the practice limit of {limit} AI requests per {window}."
            : $"You've reached the limit of {limit} AI requests per {window}.";

        if (retryAfter.HasValue)
        {
            var mins = Math.Max(1, (int)Math.Ceiling(retryAfter.Value.TotalMinutes));
            var hours = mins / 60;
            msg += mins == 1 ? " Try again in about a minute."
                 : hours >= 2 ? $" Try again in about {hours} hours."
                 : $" Try again in about {mins} minutes.";
        }

        // Named explicitly, because the complaint was that a practice 429 read as the site being down.
        if (bucket == AiBucket.Practice)
            msg += " Resume tools, cover letters and job analysis are unaffected.";

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
