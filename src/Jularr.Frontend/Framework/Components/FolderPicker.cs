using System.Text.Encodings.Web;
using Microsoft.AspNetCore.WebUtilities;

namespace Jularr.Frontend.Framework
{
    public sealed class FolderPicker(string browseUrl, string selectedPath, string fieldName, string fallbackUrl, IReadOnlyList<IUiComponent>? fallback = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Div(
                new P(new Strong(new Text("storage.paths.selected")), new ValueText(": "),
                    new Span(new ValueText("/" + selectedPath)).Attribute("data-folder-selection", "")),
                new A(new Text("storage.paths.browse")).Class("button").Attribute("href", fallbackUrl)
                    .Attribute("data-folder-open", "").Attribute("data-folder-url", QueryHelpers.AddQueryString(browseUrl, "path", selectedPath)),
                new Div([.. fallback ?? []]).Attribute("data-folder-fallback", ""),
                new Dialog(
                    new Div(new H2(new Text("storage.paths.browse")),
                        new Button(new Text("folderPicker.close")).Attribute("data-folder-close", "")).Class("folder-picker__heading"),
                    new P(new Text("folderPicker.loading")).Attribute("role", "status").Attribute("data-folder-status", "").Attribute("hidden", "hidden"),
                    new Div().Attribute("data-folder-content", "")
                ).Class("folder-picker__dialog").Attribute("aria-label", texts["storage.paths.browse"])
            ).Class("folder-picker").Attribute("data-folder-picker", "").Attribute("data-folder-field", fieldName)
                .Attribute("data-folder-error", texts["folderPicker.failed"]).Attribute("data-folder-loading", texts["folderPicker.loading"])
                .WriteTo(writer, encoder, texts);
        }

        public static IUiComponent Directories(string browseUrl, string path, IReadOnlyList<string> names, UiText texts, string? afterName = null, bool hasMore = false)
        {
            var trail = new List<IUiComponent>
            {
                new A(new Text("storage.paths.connectionRoot")).Attribute("href", QueryHelpers.AddQueryString(browseUrl, "path", "")).Attribute("data-folder-navigate", "")
            };
            var segments = path.Length == 0 ? [] : path.Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                trail.Add(new A(new ValueText(segments[index])).Attribute("href", QueryHelpers.AddQueryString(browseUrl, "path", string.Join('/', segments[..(index + 1)])))
                    .Attribute("data-folder-navigate", ""));
            }

            return new Section(
                new Nav([.. trail]).Class("folder-picker__trail").Attribute("aria-label", texts["storage.paths.selected"]),
                names.Count == 0 ? new P(new Text(afterName is null ? "storage.paths.noFolders" : "folderPicker.end"))
                    : new Ul([.. names.Select(name => new Li(new A(new ValueText(name))
                        .Attribute("href", QueryHelpers.AddQueryString(browseUrl, "path", path.Length == 0 ? name : path + "/" + name))
                        .Attribute("data-folder-navigate", "")))]).Class("folder-picker__folders"),
                new Div(
                    afterName is null ? new Span() : new A(new Text("folderPicker.first"))
                        .Attribute("href", QueryHelpers.AddQueryString(browseUrl, "path", path)).Attribute("data-folder-navigate", ""),
                    !hasMore || names.Count == 0 ? new Span() : new A(new Text("folderPicker.more"))
                        .Attribute("href", QueryHelpers.AddQueryString(browseUrl, new Dictionary<string, string?> { ["path"] = path, ["afterName"] = names[^1] }))
                        .Attribute("data-folder-navigate", "")
                ).Class("page-actions"),
                new Button(new Text("folderPicker.choose")).Primary().Attribute("data-folder-choose", path)
            ).Attribute("data-folder-fragment", "");
        }

        private sealed class Dialog(params IUiComponent[] content) : HtmlElement("dialog", content);
    }
}
