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
/// Wires ASP.NET Core's built-in rate limiter with a single named policy, <see cref="PolicyName"/>,
/// partitioned by the signed-in user's id (falling back to the client IP for anonymous callers,
/// which the [Authorize] filters normally turn away first). Rejections are answered with a friendly
/// payload instead of a bare 429: JSON <c>{ success:false, error }</c> for fetch() callers, or a
/// redirect back to the referring page with an error toast for plain form posts.
/// </summary>
public static class AiRateLimiting
{
    public const string PolicyName = "ai";

    public static IServiceCollection AddAiRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AiRateLimitOptions>(config.GetSection(AiRateLimitOptions.SectionName));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PolicyName, httpContext =>
            {
                var opts   = httpContext.RequestServices.GetRequiredService<IOptions<AiRateLimitOptions>>().Value;
                var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
                var key    = userId ?? ("ip:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
                var limit  = IsDemoUser(httpContext, config) ? opts.DemoPermitLimit : opts.PermitLimit;

                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit          = Math.Max(1, limit),
                    Window               = TimeSpan.FromMinutes(Math.Max(1, opts.WindowMinutes)),
                    QueueLimit           = 0,
                    AutoReplenishment    = true
                });
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

    private static bool IsDemoUser(HttpContext http, IConfiguration config)
    {
        var demoEmail = config["Demo:Email"];
        if (string.IsNullOrWhiteSpace(demoEmail)) return false;
        var email = http.User.FindFirstValue(ClaimTypes.Email) ?? http.User.Identity?.Name;
        return string.Equals(email, demoEmail, StringComparison.OrdinalIgnoreCase);
    }

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
