using System.Text.Encodings.Web;
using Microsoft.AspNetCore.WebUtilities;

namespace Jularr.Frontend.Framework
{
    public sealed class FolderPicker(string browseUrl, string selectedPath, string fieldName, string fallbackUrl, IReadOnlyList<IUiComponent>? fallback = null,
        string? storageMountFieldName = null, bool isVisible = true, bool choosesStorage = false, string? selectedLabel = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // The chosen folder in a path box with the browse button beside it, as in the mockups.
            var picker = new Div(
                new Div(
                    new Div(new Span(new Text("storage.paths.selected")).Class("visually-hidden"), new Span(new ValueText(selectedLabel ?? "/" + selectedPath)).Attribute("data-folder-selection", "")).Class("folder-picker__path"),
                    new A(new Text("storage.paths.browse")).Class("button button--compact").Attribute("href", fallbackUrl)
                        .Attribute("data-folder-open", "").Attribute("data-navigation", "full").Attribute("data-folder-url", QueryHelpers.AddQueryString(browseUrl, "path", selectedPath))).Class("folder-picker__row"),
                new NoScript(new Div([.. fallback ?? []])),
                new Dialog(
                    new Div(new H2(new Text("storage.paths.browse")),
                        new Button(new Text("folderPicker.close")).Attribute("data-folder-close", "")).Class("folder-picker__heading"),
                    new P(new Text("folderPicker.loading")).Attribute("role", "status").Attribute("data-folder-status", "").Attribute("hidden", "hidden"),
                    new Div().Attribute("data-folder-content", "")
                ).Class("folder-picker__dialog").Attribute("aria-label", texts["storage.paths.browse"])
            ).Class("folder-picker").Attribute("data-folder-picker", "").Attribute("data-folder-field", fieldName)
                .Attribute("data-folder-mount-field", storageMountFieldName ?? "")
                .Attribute("data-folder-chooses-storage", choosesStorage ? "true" : "false")
                .Attribute("data-folder-error", texts["folderPicker.failed"]).Attribute("data-folder-loading", texts["folderPicker.loading"]);
            if (!isVisible) picker.Attribute("hidden", "hidden");
            picker.WriteTo(writer, encoder, texts);
        }

        public static IUiComponent Directories(string browseUrl, string path, IReadOnlyList<string> names, UiText texts, string? afterName = null, bool hasMore = false,
            string? mountsUrl = null, long? storageMountId = null, string? selectedLabel = null, string? warningKey = null)
        {
            var trail = new List<IUiComponent>
            {
                new A(new Text("storage.paths.connectionRoot")).Attribute("href", QueryHelpers.AddQueryString(browseUrl, "path", "")).Attribute("data-folder-navigate", "")
            };
            if (mountsUrl is not null) trail.Insert(0, new A(new Text("folderPicker.storageConnections")).Attribute("href", mountsUrl).Attribute("data-folder-navigate", ""));
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
                warningKey is not null ? new P(new Text(warningKey)).Attribute("role", "status")
                    : new Button(new Text("folderPicker.choose")).Primary().Attribute("data-folder-choose", path)
                        .Attribute("data-folder-mount-id", storageMountId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "")
                        .Attribute("data-folder-label", selectedLabel ?? "/" + path)
            ).Attribute("data-folder-fragment", "");
        }

        private sealed class Dialog(params IUiComponent[] content) : HtmlElement("dialog", content);
        private sealed class NoScript(params IUiComponent[] content) : HtmlElement("noscript", content);
    }
}
