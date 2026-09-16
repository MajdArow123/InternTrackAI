using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Services;

/// <summary>
/// Settings for the password-reset limits. Bound from <c>RateLimiting:PasswordReset</c>
/// (<c>RateLimiting__PasswordReset__PerIpPerHour</c> etc. on Railway).
/// </summary>
public class PasswordResetRateLimitOptions
{
    public const string SectionName = "RateLimiting:PasswordReset";

    /// <summary>Reset requests one client may make per window, whatever address they ask about.</summary>
    public int PerIpPerHour { get; set; } = 5;

    /// <summary>Emails actually sent to one address per window, however many clients ask.</summary>
    public int PerAddressPerHour { get; set; } = 2;

    public int WindowMinutes { get; set; } = 60;
}

/// <summary>
/// Two fixed-window buckets guarding <c>/Identity/Account/ForgotPassword</c>, which is anonymous and spends
/// real Resend quota. Same built-in <see cref="System.Threading.RateLimiting"/> primitive as
/// <see cref="AiUsageLimiter"/>, but permits are taken **inside the page** rather than through the
/// <c>"ai"</c> HTTP policy: that policy answers a rejection with a 429 and a toast, and this page must answer
/// a rejection with the identical confirmation it shows on success, or the limit itself becomes the oracle —
/// "this address is rate limited" would mean "this address exists". Taking the permit in the page model makes
/// the rejection path literally the same <c>return Page()</c> as the success path.
///
/// <para><b>Per IP</b> bounds what one client can spend. <see cref="Allow"/> is called before the account
/// lookup, so probing unknown addresses costs the prober just as much as real ones.</para>
///
/// <para><b>Per address</b> bounds what one mailbox can receive, so a mob of clients can't flood one person.
/// It is only taken when a message is really about to go out, so probes for unknown or demo addresses can't
/// burn a real user's allowance. The trade-off is that someone who knows your address can still use up your
/// two sends for the hour; at two an hour a real user who asks twice is unaffected, and the alternative —
/// no per-address cap — lets one address be flooded from a botnet.</para>
///
/// <para><b>Addresses are never stored.</b> The partition key is a SHA-256 of the normalised address under a
/// salt generated fresh in this process, so what sits in memory is a hash nobody can reverse or correlate to
/// a mailbox, and the mapping dies with the process. Normalisation is trim + upper-invariant, matching
/// Identity's own normalised columns, so <c>A@x.com</c> and <c>a@x.com</c> share one bucket.</para>
/// </summary>
public sealed class PasswordResetLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<(string Key, int Limit)> _limiter;
    private readonly IOptionsMonitor<PasswordResetRateLimitOptions> _options;

    // Per process, never persisted: the address hashes are meaningless outside this process's lifetime.
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);

    public PasswordResetLimiter(IOptionsMonitor<PasswordResetRateLimitOptions> options)
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

    /// <summary>One reset request from this client. False once the client has spent its window's allowance.</summary>
    public bool Allow(IPAddress? clientIp)
    {
        using var lease = _limiter.AttemptAcquire((IpKey(clientIp), _options.CurrentValue.PerIpPerHour), 1);
        return lease.IsAcquired;
    }

    /// <summary>One message to this address. Call it immediately before sending, never before.</summary>
    public bool AllowSendTo(string email)
    {
        using var lease = _limiter.AttemptAcquire((AddressKey(email), _options.CurrentValue.PerAddressPerHour), 1);
        return lease.IsAcquired;
    }

    /// <summary>
    /// The client's bucket. Behind Railway's proxy <c>UseForwardedHeaders</c> has already replaced
    /// <c>RemoteIpAddress</c> with the real client address, so this is the caller and not the load balancer.
    /// IPv6 is collapsed to its /64: a single subscriber is routinely handed 2^64 addresses, and keying on the
    /// full address would let one of them rotate past the limit for free.
    /// </summary>
    public static string IpKey(IPAddress? ip)
    {
        if (ip is null) return "ip:unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return "ip6:" + new IPAddress(bytes);
        }

        return "ip4:" + ip;
    }

    /// <summary>Salted hash of the normalised address — the address itself never becomes a dictionary key.</summary>
    private string AddressKey(string email)
    {
        var normalized = Encoding.UTF8.GetBytes(email.Trim().ToUpperInvariant());
        var salted = new byte[_salt.Length + normalized.Length];
        _salt.CopyTo(salted, 0);
        normalized.CopyTo(salted, _salt.Length);
        return "addr:" + Convert.ToHexString(SHA256.HashData(salted));
    }

    public void Dispose() => _limiter.Dispose();
}
