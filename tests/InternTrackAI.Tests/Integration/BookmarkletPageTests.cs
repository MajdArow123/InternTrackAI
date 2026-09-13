using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using InternTrackAI.Models.ViewModels;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InternTrackAI.Tests.Integration;

/// <summary>The bookmarklet install page must emit working, origin-correct bookmark code.</summary>
public class BookmarkletPageTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public BookmarkletPageTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Install_page_embeds_bookmark_code_built_from_the_request_origin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Http.RegisterAsync(client);

        var res  = await client.GetAsync("/Profile/Bookmarklet");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();

        // The test host serves http://localhost, so that is the origin the code must target.
        // Razor's encoder escapes more characters than HttpUtility would, so decode what was
        // rendered and compare the decoded values instead of guessing the exact escaping.
        var expected = new BookmarkletViewModel { BaseUrl = "http://localhost" }.Code;
        var href = Regex.Match(html, "<a href=\"([^\"]+)\" class=\"btn btn-primary bookmarklet-btn\"").Groups[1].Value;
        Assert.Equal(expected, HttpUtility.HtmlDecode(href));
        var textarea = Regex.Match(html, "<textarea[^>]*id=\"bookmarkletCode\"[^>]*>([^<]*)</textarea>").Groups[1].Value;
        Assert.Equal(expected, HttpUtility.HtmlDecode(textarea));
        Assert.Contains("draggable=\"true\"", html);
        Assert.Contains("id=\"copyBookmarkletBtn\"", html);
    }

    [Fact]
    public void Bookmark_code_only_forwards_href_and_title_to_Capture()
    {
        var code = new BookmarkletViewModel { BaseUrl = "https://interntrackai.example" }.Code;

        Assert.StartsWith("javascript:", code);
        Assert.Contains("window.open('https://interntrackai.example/Capture?url='+encodeURIComponent(location.href)+'&title='+encodeURIComponent(document.title),'_blank')", code);
        // No DOM scraping: the only page state the bookmarklet reads is the address and the title.
        Assert.DoesNotContain("querySelector", code);
        Assert.DoesNotContain("innerText", code);
        Assert.DoesNotContain("fetch(", code);
    }

    [Fact]
    public async Task Install_page_requires_sign_in()
    {
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await anon.GetAsync("/Profile/Bookmarklet");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Identity/Account/Login", res.Headers.Location!.ToString());
    }
}
