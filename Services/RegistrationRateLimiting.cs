using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Services;

/// <summary>
/// Settings for the registration limit. Bound from <c>RateLimiting:Registration</c>
/// (<c>RateLimiting__Registration__PerIpPerHour</c> etc. on Railway).
/// </summary>
public class RegistrationRateLimitOptions
{
    public const string SectionName = "RateLimiting:Registration";

    /// <summary>Registration attempts one client may make per window.</summary>
    public int PerIpPerHour { get; set; } = 5;

    public int WindowMinutes { get; set; } = 60;
}

/// <summary>
/// One fixed-window bucket per client guarding <c>/Identity/Account/Register</c>. Registration is
/// anonymous, needs no confirmed email and signs the visitor straight in, so without a bound an
/// account can be minted in a loop — and because the AI limiter partitions on user id, a fresh
/// account resets the AI quota. The AI limiter is sound; what was unbounded is the supply of
/// identities.
///
/// <para>Same built-in <see cref="System.Threading.RateLimiting"/> primitive as
/// <see cref="AiUsageLimiter"/> and <see cref="PasswordResetLimiter"/>, and, like the password-reset
/// one, the permit is taken <b>inside the page</b> rather than through the <c>"ai"</c> HTTP policy.
/// That policy's rejection path redirects a form post back to the referrer with a toast, which on a
/// page the visitor has never successfully submitted reads as the form silently losing their input;
/// the page model instead re-renders the form with the reason on it, under a 429.</para>
///
/// <para>The permit is taken once the posted form is otherwise valid and before the account is
/// created, so it bounds attempts rather than successes. The trade-off is that a visitor who trips
/// Identity's password rules several times spends permits on those failures; at five an hour that
/// leaves room for the mistakes the client-side validation doesn't already catch. Unlike
/// ForgotPassword there is no oracle to protect here — Register already has to say "that address is
/// taken" — so the refusal can be explicit.</para>
///
/// <para>Keyed by <see cref="PasswordResetLimiter.IpKey"/> so "one client" means the same thing on
/// both anonymous endpoints, IPv6 /64 collapsing included.</para>
/// </summary>
public sealed class RegistrationLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly IOptionsMonitor<RegistrationRateLimitOptions> _options;

    public RegistrationLimiter(IOptionsMonitor<RegistrationRateLimitOptions> options)
    {
        _options = options;
        _limiter = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit       = Math.Max(1, _options.CurrentValue.PerIpPerHour),
                Window            = TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.WindowMinutes)),
                QueueLimit        = 0,
                AutoReplenishment = true
            }));
    }

    public int WindowMinutes => Math.Max(1, _options.CurrentValue.WindowMinutes);

    /// <summary>
    /// One registration attempt from this client. False once the client has spent its window's
    /// allowance; <paramref name="retryAfter"/> is how long until the window rolls over, when the
    /// limiter can say.
    /// </summary>
    public bool Allow(IPAddress? clientIp, out TimeSpan? retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(PasswordResetLimiter.IpKey(clientIp), 1);
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? ra : null;
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
