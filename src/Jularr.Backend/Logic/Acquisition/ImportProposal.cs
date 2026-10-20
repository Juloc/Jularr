using System.Text.Json;

namespace Jularr.Backend.Logic.Acquisition
{
    // Where a download's finished files lie: on a storage connection or in a folder of the server, below this directory.
    internal sealed record ImportSourceRef(long? StorageMountId, string? LocalRoot, string Directory);

    internal sealed record ImportProposalDocument(ImportSourceRef Source, IReadOnlyList<ImportFileProposal> Files);

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
    }
}
