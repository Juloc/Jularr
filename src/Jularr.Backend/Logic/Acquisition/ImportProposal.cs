using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jularr.Backend.Logic.Acquisition
{
    // Where a download's finished files lie: on a storage connection or in a folder of the server, below this directory.
    internal sealed record ImportSourceRef(long? StorageMountId, string? LocalRoot, string Directory);

    // Handled: the files a person already imported by hand, while the download keeps waiting for the rest.
    internal sealed record ImportProposalDocument(ImportSourceRef Source, IReadOnlyList<ImportFileProposal> Files, IReadOnlyList<string>? Handled = null);

    // What a waiting download keeps for a person: the files with where their names say they belong, and where they are, so they can be imported by hand.
    internal static class ImportProposal
    {
        public static string Write(ImportSourceRef source, IReadOnlyList<ImportFileProposal>? files) => JsonSerializer.Serialize(new ImportProposalDocument(source, files ?? []));

        public static ImportSourceRef? ReadSource(string? json)
        {
            if (json is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Source", out var source) ? source.Deserialize<ImportSourceRef>() : null;
        }

        // The files of the download a person already imported by hand.
        public static IReadOnlyList<string> ReadHandled(string? json)
        {
            if (json is null)
            {
                return [];
            }

            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Handled", out var handled) && handled.ValueKind == JsonValueKind.Array
                ? handled.Deserialize<string[]>() ?? [] : [];
        }

        // The proposal with the files handled so far; the rest of it stays as it was.
        public static string WithHandled(string json, IEnumerable<string> handled)
        {
            var document = JsonNode.Parse(json)!.AsObject();
            document["Handled"] = JsonSerializer.SerializeToNode(handled.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            return document.ToJsonString();
        }
    }
}
