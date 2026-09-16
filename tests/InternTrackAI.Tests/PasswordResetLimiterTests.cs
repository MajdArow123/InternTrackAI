using System.Net;
using InternTrackAI.Services;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Tests;

/// <summary>
/// The two buckets in isolation. The page-level behaviour (that a rejection is indistinguishable from a
/// send) lives in <c>Integration/PasswordResetRateLimitTests.cs</c>; this file is about the counting.
/// </summary>
public class PasswordResetLimiterTests
{
    private static PasswordResetLimiter Build(int perIp = 5, int perAddress = 2) =>
        new(new StaticOptions(new PasswordResetRateLimitOptions
        {
            PerIpPerHour = perIp,
            PerAddressPerHour = perAddress,
            WindowMinutes = 60
        }));

    private sealed class StaticOptions(PasswordResetRateLimitOptions value)
        : IOptionsMonitor<PasswordResetRateLimitOptions>
    {
        public PasswordResetRateLimitOptions CurrentValue => value;
        public PasswordResetRateLimitOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PasswordResetRateLimitOptions, string?> listener) => null;
    }

    private static readonly IPAddress Ip = IPAddress.Parse("203.0.113.7");

    // ── Per client ───────────────────────────────────────────────────────────

    [Fact]
    public void A_client_gets_exactly_its_allowance_then_is_refused()
    {
        using var limiter = Build(perIp: 5);

        for (var i = 1; i <= 5; i++)
            Assert.True(limiter.Allow(Ip), $"request {i} should have been allowed");

        Assert.False(limiter.Allow(Ip));
        Assert.False(limiter.Allow(Ip));
    }

    [Fact]
    public void One_client_running_out_does_not_affect_another()
    {
        using var limiter = Build(perIp: 2);
        var other = IPAddress.Parse("198.51.100.4");

        Assert.True(limiter.Allow(Ip));
        Assert.True(limiter.Allow(Ip));
        Assert.False(limiter.Allow(Ip));

        Assert.True(limiter.Allow(other));
    }

    [Fact]
    public void Clients_without_an_address_share_one_bucket_rather_than_escaping_the_limit()
    {
        using var limiter = Build(perIp: 2);

        Assert.True(limiter.Allow(null));
        Assert.True(limiter.Allow(null));
        Assert.False(limiter.Allow(null));
    }

    [Fact]
    public void An_IPv6_client_cannot_rotate_through_its_own_subnet_to_get_more()
    {
        using var limiter = Build(perIp: 2);

        // A single subscriber is routinely handed a whole /64; all of it counts as one client.
        Assert.True(limiter.Allow(IPAddress.Parse("2001:db8:1234:5678::1")));
        Assert.True(limiter.Allow(IPAddress.Parse("2001:db8:1234:5678::dead:beef")));
        Assert.False(limiter.Allow(IPAddress.Parse("2001:db8:1234:5678:aaaa:bbbb:cccc:dddd")));

        // A genuinely different /64 is a different client.
        Assert.True(limiter.Allow(IPAddress.Parse("2001:db8:1234:9999::1")));
    }

    [Fact]
    public void An_IPv4_mapped_address_is_the_same_client_as_the_plain_IPv4_one()
    {
        using var limiter = Build(perIp: 1);

        Assert.True(limiter.Allow(IPAddress.Parse("203.0.113.9")));
        Assert.False(limiter.Allow(IPAddress.Parse("::ffff:203.0.113.9")));
    }

    [Theory]
    [InlineData("203.0.113.7", "ip4:203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "ip4:203.0.113.7")]
    [InlineData("2001:db8:1234:5678:aaaa::1", "ip6:2001:db8:1234:5678::")]
    public void IpKey_collapses_the_forms_that_are_really_one_client(string address, string expected) =>
        Assert.Equal(expected, PasswordResetLimiter.IpKey(IPAddress.Parse(address)));

    [Fact]
    public void IpKey_tolerates_a_missing_address() => Assert.Equal("ip:unknown", PasswordResetLimiter.IpKey(null));

    // ── Per address ──────────────────────────────────────────────────────────

    [Fact]
    public void An_address_gets_exactly_its_allowance_then_is_refused()
    {
        using var limiter = Build(perAddress: 2);

        Assert.True(limiter.AllowSendTo("someone@example.test"));
        Assert.True(limiter.AllowSendTo("someone@example.test"));
        Assert.False(limiter.AllowSendTo("someone@example.test"));
    }

    [Fact]
    public void The_same_mailbox_written_differently_shares_one_bucket()
    {
        using var limiter = Build(perAddress: 2);

        // Identity normalises to upper-invariant; the limiter has to agree or the cap is trivially bypassed.
        Assert.True(limiter.AllowSendTo("Someone@Example.test"));
        Assert.True(limiter.AllowSendTo("  SOMEONE@EXAMPLE.TEST  "));
        Assert.False(limiter.AllowSendTo("someone@example.test"));
    }

    [Fact]
    public void Different_addresses_are_counted_apart()
    {
        using var limiter = Build(perAddress: 1);

        Assert.True(limiter.AllowSendTo("a@example.test"));
        Assert.False(limiter.AllowSendTo("a@example.test"));
        Assert.True(limiter.AllowSendTo("b@example.test"));
    }

    [Fact]
    public void The_two_buckets_are_independent()
    {
        using var limiter = Build(perIp: 5, perAddress: 2);

        // Spending the address allowance leaves the client's own allowance untouched, and vice versa.
        Assert.True(limiter.AllowSendTo("a@example.test"));
        Assert.True(limiter.AllowSendTo("a@example.test"));
        Assert.False(limiter.AllowSendTo("a@example.test"));

        for (var i = 1; i <= 5; i++) Assert.True(limiter.Allow(Ip));
        Assert.False(limiter.Allow(Ip));
    }

    [Fact]
    public void Two_processes_hash_the_same_address_to_different_keys()
    {
        // The salt is per process, so what lives in memory can't be correlated back to a mailbox
        // (nor across restarts). Both limiters still count that address correctly on their own.
        using var one = Build(perAddress: 1);
        using var two = Build(perAddress: 1);

        Assert.True(one.AllowSendTo("shared@example.test"));
        Assert.False(one.AllowSendTo("shared@example.test"));
        Assert.True(two.AllowSendTo("shared@example.test"));
    }
}
