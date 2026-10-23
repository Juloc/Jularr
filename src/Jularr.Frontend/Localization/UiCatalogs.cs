using System.Reflection;
using System.Text.Json;
using Jularr.Backend.Service.Core.Localization;

namespace Jularr.Frontend.Localization
{
    // The languages Jularr ships, one JSON file each in Catalogs: locale, name, whether it is reviewed and enabled, and every key with its English and its text.
    public static class UiCatalogs
    {
        public static IReadOnlyList<UiTextCatalog> Load()
        {
            var assembly = typeof(UiCatalogs).Assembly;
            return [.. assembly.GetManifestResourceNames().Where(name => name.Contains(".Localization.Catalogs.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Select(name => Read(assembly, name))];
        }

        private static UiTextCatalog Read(Assembly assembly, string name)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var texts = root.GetProperty("texts").EnumerateObject().ToDictionary(entry => entry.Name,
                entry => (entry.Value[0].GetString()!, entry.Value[1].GetString()!), StringComparer.Ordinal);
            return new UiTextCatalog(root.GetProperty("locale").GetString()!, root.GetProperty("name").GetString()!, root.TryGetProperty("rightToLeft", out var rtl) && rtl.GetBoolean(),
                root.GetProperty("reviewed").GetBoolean(), root.GetProperty("enabled").GetBoolean(), texts);
        }
    }
}
