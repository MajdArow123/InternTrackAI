namespace InternTrackAI.Tests;

/// <summary>
/// Structural checks over <c>wwwroot/js/practice.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Crude on purpose, and the only coverage this file has.</b> The practice page's client behaviour
/// has no test runner (CLAUDE.md §10), so a defect there passes a fully green suite — which has now
/// happened twice, both times leaving the progress card reporting numbers from page load while the
/// cards above it said otherwise. String matching cannot prove the refresh works; it can prove the call
/// has not been deleted, which is what actually went wrong.
/// </para>
/// <para>
/// Same trade-off as <c>TourTests</c>, which string-matches <c>tour-steps.js</c> rather than pulling in
/// a JavaScript parser. If these get fussy, the right answer is a Practice dimension in <c>e2e/</c>,
/// not a cleverer regex.
/// </para>
/// </remarks>
public class PracticeScriptTests
{
    private static string Script()
    {
        // The test project shadow-copies, so walk up to the repo root rather than using the base dir.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InternTrackAI.csproj")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "wwwroot", "js", "practice.js");
        Assert.True(File.Exists(path), $"practice.js not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Generating_questions_refreshes_the_progress_card()
    {
        // The bug: generate() appended cards and never touched the progress card, so two successful
        // generations left it reporting the count from page load. Reported from production as
        // "ten questions stored, the progress card shows 5" — the questions were fine, the number was
        // a page-load snapshot.
        var js = Script();

        var appended = js.IndexOf("added[added.length - 1].scrollIntoView", StringComparison.Ordinal);
        Assert.True(appended > 0, "could not find the generate success path");

        var tail = js[appended..Math.Min(js.Length, appended + 900)];
        Assert.Contains("practiceRefreshProgress", tail);
    }

    [Fact]
    public void Answering_refreshes_the_progress_card()
    {
        // The first instance of the same bug, fixed by returning the re-rendered card with the answer.
        var js = Script();

        // Anchored on the single-answer path specifically. The batch also calls replaceWith and
        // deliberately ignores data.progress — it refreshes once at the end instead — so matching the
        // first replaceWith in the file finds the wrong one.
        var swapped = js.IndexOf("Answering changes every number on the progress card", StringComparison.Ordinal);
        Assert.True(swapped > 0, "could not find the single-answer progress swap");

        Assert.Contains("data.progress", js[swapped..Math.Min(js.Length, swapped + 600)]);
    }

    [Fact]
    public void The_batch_refreshes_the_progress_card_once_it_settles()
    {
        var js = Script();

        var settled = js.IndexOf("Promise.all(pool).then", StringComparison.Ordinal);
        Assert.True(settled > 0, "could not find the batch completion handler");

        Assert.Contains("practiceRefreshProgress", js[settled..Math.Min(js.Length, settled + 900)]);
    }

    [Fact]
    public void There_is_one_definition_of_the_progress_refresh()
    {
        // It was inlined in the batch and missing from generate, which is how the two paths disagreed.
        var js = Script();

        var fetches = js.Split("'/Practice/Progress'").Length - 1;
        Assert.True(fetches == 1, $"expected one place to fetch the progress card, found {fetches}");
    }
}
