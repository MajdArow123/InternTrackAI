using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// The normalisation behind <c>Email:BaseUrl</c>. The end-to-end behaviour (a spoofed host cannot change
/// the emailed link, an unusable value falls back to the request) lives in
/// <c>Integration/ForwardedProtoTests.cs</c>; these pin the shape of the value itself, because a wrong
/// origin here ships in an email nobody can recall.
/// </summary>
public class EmailLinkBaseTests
{
    [Theory]
    [InlineData("https://interntrackai.majdarow.com", "https://interntrackai.majdarow.com")]
    [InlineData("https://interntrackai.majdarow.com/", "https://interntrackai.majdarow.com")]
    [InlineData("  https://interntrackai.majdarow.com///  ", "https://interntrackai.majdarow.com")]
    [InlineData("http://localhost:5240", "http://localhost:5240")]           // local verification
    [InlineData("https://example.test/app/", "https://example.test/app")]    // a sub-path deployment keeps its path
    [InlineData("https://example.test/?utm=1", "https://example.test")]      // query and fragment are typos here
    [InlineData("https://example.test/#x", "https://example.test")]
    public void A_usable_origin_is_normalised_without_a_trailing_slash(string configured, string expected) =>
        Assert.Equal(expected, EmailLinkBase.Resolve(configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("interntrackai.majdarow.com")]   // no scheme: not absolute
    [InlineData("/Identity/Account")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.test")]
    [InlineData("mailto:someone@example.test")]
    public void Anything_that_is_not_an_absolute_http_origin_is_treated_as_unset(string? configured) =>
        Assert.Null(EmailLinkBase.Resolve(configured));

    [Theory]
    [InlineData("https://x.test", "/Identity/Account/ResetPassword?code=abc", "https://x.test/Identity/Account/ResetPassword?code=abc")]
    [InlineData("https://x.test", "Identity/Account/ResetPassword", "https://x.test/Identity/Account/ResetPassword")]
    [InlineData("https://x.test/app", "/Identity/Account/ResetPassword", "https://x.test/app/Identity/Account/ResetPassword")]
    public void Combine_joins_on_exactly_one_slash(string resolvedBase, string relative, string expected) =>
        Assert.Equal(expected, EmailLinkBase.Combine(resolvedBase, relative));
}
