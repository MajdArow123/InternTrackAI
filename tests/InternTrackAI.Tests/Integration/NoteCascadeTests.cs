using System.Net;
using InternTrackAI.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

public class NoteCascadeTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public NoteCascadeTests(TestAppFactory factory) => _factory = factory;

    /// <summary>Registers a user, creates an application through the UI flow, adds two notes, and returns the app id.</summary>
    private async Task<(HttpClient Client, string Token, int AppId)> SetupAppWithNotesAsync(string company)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Http.RegisterAsync(client);
        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");

        var create = await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CompanyName"] = company, ["RoleTitle"] = "Intern", ["Status"] = "Saved", ["WorkMode"] = "Remote", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);

        int appId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            appId = (await db.JobApplications.SingleAsync(a => a.CompanyName == company)).Id;
        }

        foreach (var text in new[] { "first note", "second note" })
        {
            var add = await client.PostAsync("/JobApplications/AddNote", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["appId"] = appId.ToString(), ["text"] = text, ["__RequestVerificationToken"] = token,
            }));
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }
        Assert.Equal(2, await NoteCount(appId));
        return (client, token, appId);
    }

    private async Task<int> NoteCount(int appId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ApplicationNotes.CountAsync(n => n.JobApplicationId == appId);
    }

    [Fact]
    public async Task Deleting_an_application_deletes_its_notes()
    {
        var (client, token, appId) = await SetupAppWithNotesAsync("Cascade Single Co");

        var del = await client.PostAsync($"/JobApplications/Delete/{appId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Id"] = appId.ToString(), ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);

        Assert.Equal(0, await NoteCount(appId));
        using var scope = _factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().JobApplications.AnyAsync(a => a.Id == appId));
    }

    [Fact]
    public async Task Bulk_deleting_applications_deletes_their_notes()
    {
        var (client, token, appId) = await SetupAppWithNotesAsync("Cascade Bulk Co");

        var del = await client.PostAsync("/JobApplications/BulkDelete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ids"] = appId.ToString(), ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);

        Assert.Equal(0, await NoteCount(appId));
    }

    [Fact]
    public void Migration_declares_a_cascading_foreign_key_on_notes()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var fk = db.Model.FindEntityType(typeof(Models.ApplicationNote))!.GetForeignKeys().Single();
        Assert.Equal(typeof(Models.JobApplication), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }
}
