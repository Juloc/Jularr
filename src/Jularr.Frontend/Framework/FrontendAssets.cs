using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Jularr.Frontend.Framework;

// Release assets are compiled by Vite; read the manifest once for the process lifetime.
// Never hash or compile assets on a request, or guess published asset names.
public sealed class FrontendAssets(IWebHostEnvironment environment)
{
    private sealed record Entry
    {
        public string? File { get; init; }
        public string[]? Css { get; init; }
        public string[]? Imports { get; init; }
    }

    private readonly Lazy<IReadOnlyDictionary<string, Entry>> _manifest = new(() =>
    {
        var file = Path.Combine(environment.WebRootPath, "build", ".vite", "manifest.json");
        if (!File.Exists(file))
            throw new InvalidOperationException("Frontend asset manifest is missing. Run npm ci and npm run build in src/Jularr.Frontend.");

        var catalog = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
            File.ReadAllText(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The frontend asset manifest is invalid.");

        return catalog;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public IHtmlContent Styles(params string[] entries)
    {
        var builder = new HtmlContentBuilder();
        var visitedEntries = new HashSet<string>(StringComparer.Ordinal);
        var styles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries) AddStyles(entry, visitedEntries, styles);

        foreach (var style in styles)
        {
            var tag = new TagBuilder("link");
            tag.Attributes["rel"] = "stylesheet";
            tag.Attributes["href"] = AssetUrl(style);
            builder.AppendHtml(tag);
        }

        return builder;
    }

    public IHtmlContent Scripts(params string[] entries)
    {
        var builder = new HtmlContentBuilder();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in entries)
        {
            var entry = Resolve(source);
            if (entry.File is not { } file || !file.EndsWith(".js", StringComparison.Ordinal))
                continue;

            if (!emitted.Add(file)) continue;
            var tag = new TagBuilder("script");
            tag.Attributes["type"] = "module";
            tag.Attributes["src"] = AssetUrl(file);
            builder.AppendHtml(tag);
        }

        return builder;
    }

    private void AddStyles(string source, HashSet<string> visited, HashSet<string> styles)
    {
        if (!visited.Add(source)) return;
        var entry = Resolve(source);
        foreach (var import in entry.Imports ?? []) AddStyles(import, visited, styles);
        if (entry.File is { } file && file.EndsWith(".css", StringComparison.Ordinal))
            styles.Add(file);
        foreach (var css in entry.Css ?? []) styles.Add(css);
    }

    private Entry Resolve(string source) => _manifest.Value.TryGetValue(source, out var result)
        ? result
        : throw new InvalidOperationException($"Missing compiled frontend asset entry: {source}");

    private static string AssetUrl(string file)
    {
        if (string.IsNullOrEmpty(file) || file[0] == '/' || file.Contains("..", StringComparison.Ordinal)
            || file.Contains('\\') || file.Contains('?') || file.Contains('#')
            || file.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')))
            throw new InvalidOperationException("Unsafe file name in the compiled asset manifest.");

        return "/build/" + file;
    }
}
