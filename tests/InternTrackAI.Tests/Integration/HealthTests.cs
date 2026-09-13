using System.Net;
using System.Text.Json;

namespace InternTrackAI.Tests.Integration;

public class HealthTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public HealthTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_endpoint_is_anonymous_and_reports_the_database_check()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("application/json", res.Content.Headers.ContentType!.ToString());

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        var db = body.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "database");
        Assert.Equal("Healthy", db.GetProperty("status").GetString());
    }
}
