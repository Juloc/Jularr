using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    // The enabled search connections a search of one target asks, shared by the search and by the list of sources a person sees.
    internal sealed class SearchSources
    {
        private SearchSources(IReadOnlyList<AcquisitionConnectionData> connections, IReadOnlyList<AcquisitionConnectionData> indexers, IReadOnlyList<AcquisitionConnectionData> catalogs,
            IReadOnlyList<AcquisitionConnectionData> torrents, IReadOnlyList<AcquisitionConnectionData> skipped)
        {
            Indexers = indexers;
            Catalogs = catalogs;
            Torrents = torrents;
            Skipped = skipped;
            var ids = indexers.Concat(catalogs).Concat(torrents).Concat(skipped).Select(connection => connection.Id).ToHashSet();
            All = connections.Where(connection => ids.Contains(connection.Id)).ToArray();
        }

        // Newznab indexers that can be asked now.
        public IReadOnlyList<AcquisitionConnectionData> Indexers { get; }

        // Direct book and light novel sources, answering one title query each.
        public IReadOnlyList<AcquisitionConnectionData> Catalogs { get; }

        // The torrent index of the light novels.
        public IReadOnlyList<AcquisitionConnectionData> Torrents { get; }

        // Sources that serve the target but are over their query limit or, in an automatic search, backed off.
        public IReadOnlyList<AcquisitionConnectionData> Skipped { get; }

        // Every one of them in the order of the connections.
        public IReadOnlyList<AcquisitionConnectionData> All { get; }

        // An automatic search asks a direct source only for a title it can have: Project Gutenberg holds public domain books.
        // With a set of connections (a search of the sources without a feed on their rhythm) only those are asked.
        public static SearchSources Of(IReadOnlyList<AcquisitionConnectionData> connections, WantedTargetData target, bool automatic, DateTime now, IReadOnlySet<long>? only = null)
        {
            var kind = AcquisitionSelectionLogic.GetKind(target.MediaTypeId, target.MediaSubtypeId);
            var mediaType = target.MediaTypeId;
            var asked = only is null ? connections : connections.Where(connection => only.Contains(connection.Id)).ToArray();
            var allNewznab = asked.Where(connection => connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Newznab).ToArray();
            var newznab = allNewznab.Where(connection => AcquisitionConnectionLogic.CanSearch(connection, mediaType, automatic, now)).ToArray();
            var catalogs = kind is AcquisitionKind.Book or AcquisitionKind.LightNovel ? asked.Where(connection => DirectSources.IsDirect(connection.AcquisitionConnectionTypeId)
                && (kind == AcquisitionKind.LightNovel || connection.AcquisitionConnectionTypeId != AcquisitionConnectionType.Narou)
                && AcquisitionConnectionLogic.Serves(connection, mediaType, automatic) && (!automatic || CanHave(connection, target, now))).ToArray() : [];
            var nyaa = kind is AcquisitionKind.LightNovel ? asked.Where(connection => connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Nyaa).ToArray() : [];
            var torrents = nyaa.Where(connection => AcquisitionConnectionLogic.CanSearch(connection, mediaType, automatic, now)).ToArray();
            var skipped = allNewznab.Where(connection => AcquisitionConnectionLogic.Serves(connection, mediaType, automatic) && !newznab.Contains(connection))
                .Concat(nyaa.Where(connection => AcquisitionConnectionLogic.Serves(connection, mediaType, automatic) && !torrents.Contains(connection))).ToArray();
            return new SearchSources(connections, newznab, catalogs, torrents, skipped);
        }

        // Every source that an automatic search of the title can ask, whatever its backoff or limits today.
        public static IReadOnlyList<AcquisitionConnectionData> Automatic(IReadOnlyList<AcquisitionConnectionData> connections, WantedTargetData target, DateTime now)
        {
            var kind = AcquisitionSelectionLogic.GetKind(target.MediaTypeId, target.MediaSubtypeId);
            return [.. connections.Where(connection => AcquisitionConnectionLogic.Serves(connection, target.MediaTypeId, true) && (connection.AcquisitionConnectionTypeId switch
            {
                AcquisitionConnectionType.Newznab => true,
                AcquisitionConnectionType.Nyaa => kind == AcquisitionKind.LightNovel,
                AcquisitionConnectionType.Narou => kind == AcquisitionKind.LightNovel,
                _ => DirectSources.IsDirect(connection.AcquisitionConnectionTypeId) && kind is AcquisitionKind.Book or AcquisitionKind.LightNovel && CanHave(connection, target, now)
            }))];
        }

        // US public domain: published 95 years ago or earlier.
        private static bool CanHave(AcquisitionConnectionData connection, WantedTargetData target, DateTime now) =>
            connection.AcquisitionConnectionTypeId != AcquisitionConnectionType.Gutenberg || target.ReleaseYear is not { } year || year <= now.Year - 96;
    }
}
