using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace InternTrackAI.Helpers;

/// <summary>
/// Renders a Razor partial to a string so a controller can return markup from a fetch() endpoint.
/// </summary>
/// <remarks>
/// This is how the app does dynamic content (CLAUDE.md §8): the server returns rendered HTML and the
/// client appends it, instead of the client holding a second copy of the markup in a template string.
/// The Prep page's inline script is the counter-example — it re-implements the question card in
/// JavaScript, so the two can drift, which is exactly what this avoids for the practice page.
/// </remarks>
public static class PartialRenderer
{
    /// <param name="viewData">
    /// Extra entries for the partial's <c>ViewData</c>. Used for render-time presentation flags that are
    /// not part of the model — the practice card's expanded/collapsed state, for instance, which depends
    /// on whether the card was just scored rather than on anything stored about the question.
    /// </param>
    public static async Task<string> RenderPartialAsync<TModel>(
        this Controller controller, string partialName, TModel model,
        IDictionary<string, object?>? viewData = null)
    {
        var engine = controller.HttpContext.RequestServices.GetRequiredService<ICompositeViewEngine>();
        var result = engine.FindView(controller.ControllerContext, partialName, isMainPage: false);

        if (!result.Success)
            throw new InvalidOperationException($"Partial '{partialName}' not found. Searched: {string.Join(", ", result.SearchedLocations)}");

        await using var writer = new StringWriter();

        var data = new ViewDataDictionary<TModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        if (viewData is not null)
            foreach (var (key, value) in viewData) data[key] = value;

        var viewContext = new ViewContext(
            controller.ControllerContext,
            result.View,
            data,
            controller.TempData,
            writer,
            new HtmlHelperOptions());

        await result.View.RenderAsync(viewContext);
        return writer.ToString();
    }
}
