using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Servarr;
using Jularr.Integrations.Logic.Servarr;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    // A one-time takeover from Prowlarr or a Servarr: indexers, clients, quality profiles with their custom formats and monitoring, root folders and missing titles; nothing keeps running alongside.
    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/takeover")]
    public sealed class RunTakeoverV1 : AdminService<TakeoverParametersV1, RunTakeoverDataV1>
    {
        private const int TitlesPerRun = 50;
        private readonly ServarrApiClient _servarr;
        private readonly MetadataCatalog _catalog;
        private readonly IDataProtector _protector;

        internal RunTakeoverV1(ServarrApiClient servarr, MetadataCatalog catalog, IDataProtectionProvider protection)
        {
            _servarr = servarr;
            _catalog = catalog;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<TakeoverItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(TakeoverParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(TakeoverParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!Uri.TryCreate(Data.BaseUrl + "/", UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            {
                throw new DataValidationException(nameof(RunTakeoverDataV1.BaseUrl), "invalid_url");
            }

            // Everything is read before anything is written.
            var source = new ServarrConnection(address, Data.ApiKey, Data.TakeoverSourceTypeId is TakeoverSourceType.Sonarr or TakeoverSourceType.Radarr ? 3 : 1);
            IReadOnlyList<ServarrIndexer> indexers;
            IReadOnlyList<ServarrClient> clients;
            IReadOnlyList<ServarrTitle> titles;
            IReadOnlyList<ServarrQualityProfile> profiles = [];
            IReadOnlyList<ServarrCustomFormat> formats = [];
            IReadOnlyList<ServarrMetadataProfile> metadataProfiles = [];
            IReadOnlyList<ServarrRootFolder> rootFolders = [];
            try
            {
                if (Data.TakeoverSourceTypeId != TakeoverSourceType.Prowlarr)
                {
                    profiles = await _servarr.ReadQualityProfilesAsync(source, CancellationToken);
                    formats = await _servarr.ReadCustomFormatsAsync(source, CancellationToken);
                    rootFolders = await _servarr.ReadRootFoldersAsync(source, CancellationToken);
                }

                if (Data.TakeoverSourceTypeId is TakeoverSourceType.Lidarr or TakeoverSourceType.Readarr)
                {
                    metadataProfiles = await _servarr.ReadMetadataProfilesAsync(source, CancellationToken);
                }

                indexers = await _servarr.ReadIndexersAsync(source, CancellationToken);
                clients = Data.TakeoverSourceTypeId == TakeoverSourceType.Prowlarr ? [] : await _servarr.ReadClientsAsync(source, CancellationToken);
                titles = Data.TakeoverSourceTypeId switch
                {
                    TakeoverSourceType.Radarr => await _servarr.ReadMoviesAsync(source, CancellationToken),
                    TakeoverSourceType.Sonarr => await _servarr.ReadSeriesAsync(source, CancellationToken),
                    TakeoverSourceType.Lidarr => await _servarr.ReadAlbumsAsync(source, CancellationToken),
                    TakeoverSourceType.Readarr => await _servarr.ReadBooksAsync(source, CancellationToken),
                    _ => []
                };
            }
            catch (ServarrException exception)
            {
                throw new ServiceUnavailableException(exception.Failure == ServarrFailure.Unauthorized ? "acquisition_authentication_failed" : "acquisition_unavailable");
            }

            var (known, jularrProfiles, libraries) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        connection."BaseUrl"
                    FROM
                        "AcquisitionConnections" AS connection;
                    """;
                var profileSql = new SqlStatement();
                profileSql.Statement =
                    """
                    SELECT
                        profile."Id",
                        profile."Name",
                        profile."MediaTypeId"
                    FROM
                        "AcquisitionProfiles" AS profile;
                    """;
                var librarySql = new SqlStatement();
                librarySql.Statement =
                    """
                    SELECT
                        root."Id",
                        root."Name",
                        root."MediaTypeId",
                        root."RelativePath"
                    FROM
                        "LibraryRoots" AS root
                    ORDER BY
                        root."Id";
                    """;
                return (await ctx.ReadSql.ReadListAsync<AcquisitionConnectionUrlData>(sql, ct), await ctx.ReadSql.ReadListAsync<AcquisitionProfileNameData>(profileSql, ct),
                    await ctx.ReadSql.ReadListAsync<TakeoverLibraryData>(librarySql, ct));
            }, CancellationToken);
            var existing = known.Select(connection => connection.BaseUrl.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Connections whose key the source hides are completed in the connection form; open indexers come as they are.
            var items = new List<TakeoverItemV1>();
            foreach (var indexer in indexers)
            {
                items.Add(await TakeConnectionAsync(TakeoverItemType.Indexer, indexer.Name, indexer.BaseUrl, indexer.ApiKey is null && indexer.HasHiddenKey, existing,
                    new SaveAcquisitionConnectionDataV1(Limit(indexer.Name, 128), AcquisitionConnectionType.Newznab, indexer.BaseUrl, indexer.ApiKey, indexer.IsEnabled, indexer.Priority,
                        indexer.Categories, null, null, null)));
            }

            foreach (var client in clients)
            {
                items.Add(await TakeConnectionAsync(TakeoverItemType.Client, client.Name, client.BaseUrl, client.ApiKey is null, existing,
                    new SaveAcquisitionConnectionDataV1(Limit(client.Name, 128), AcquisitionConnectionType.Sabnzbd, client.BaseUrl, client.ApiKey, client.IsEnabled, client.Priority, [],
                        client.Category is { Length: <= 64 } category ? category : null, null, null)));
            }

            var (type, mediaType) = Data.TakeoverSourceTypeId switch
            {
                TakeoverSourceType.Radarr => (TakeoverItemType.Movie, MediaType.Movie),
                TakeoverSourceType.Sonarr => (TakeoverItemType.Series, MediaType.Series),
                TakeoverSourceType.Lidarr => (TakeoverItemType.Album, MediaType.Music),
                _ => (TakeoverItemType.Book, MediaType.Book)
            };

            // Profiles keep their name; one of that name and type is not taken twice. A quality profile takes the metadata profile most of its titles follow.
            var profileIds = new Dictionary<int, long>();
            var formatsById = formats.ToDictionary(format => format.Id);
            var metadataById = metadataProfiles.ToDictionary(metadata => metadata.Id);
            foreach (var profile in profiles)
            {
                var metadataId = titles.Where(title => title.QualityProfileId == profile.Id).Select(title => title.MetadataProfileId)
                    .Concat(rootFolders.Where(folder => folder.DefaultQualityProfileId == profile.Id).Select(folder => folder.DefaultMetadataProfileId))
                    .OfType<int>().Where(metadataById.ContainsKey).GroupBy(id => id).OrderByDescending(group => group.Count()).Select(group => (int?)group.Key).FirstOrDefault();
                var mapped = ServarrProfileLogic.Map(profile, formatsById, mediaType, metadataId is { } id ? metadataById[id] : null);
                var name = Limit(profile.Name, 500);
                var outcome = await Context.ExecuteBatchAsync((ctx, ct) => ctx.TakeOverProfileAsync(mapped, jularrProfiles, Data.Apply, ct), CancellationToken);
                if (outcome.ProfileId is { } profileId) profileIds[profile.Id] = profileId;
                items.Add(new TakeoverItemV1(TakeoverItemType.Profile, name, outcome.OutcomeKey, null));
                if (outcome.OutcomeKey is not ("takeover_added" or "takeover_will_add")) continue;
                if (profile.MinimumFormatScore > 0) items.Add(new TakeoverItemV1(TakeoverItemType.Profile, name, "takeover_minimum_score", null));
                items.AddRange(mapped.SkippedFormats.Select(format => new TakeoverItemV1(TakeoverItemType.Format, Limit(format, 500), "takeover_format_skipped", null)));
            }

            // Each root folder goes to a chosen library of its type. A library named like the folder, or the only one, is suggested.
            var chosen = (Data.RootFolders ?? []).Select(Choice).OfType<(long LibraryRootId, string Path)>().GroupBy(choice => Folder(choice.Path), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().LibraryRootId, StringComparer.Ordinal);
            var candidates = libraries.Where(library => library.MediaTypeId == mediaType).ToArray();
            foreach (var folder in rootFolders)
            {
                var path = Limit(folder.Path, 500);
                if (candidates.Length == 0)
                {
                    items.Add(new TakeoverItemV1(TakeoverItemType.RootFolder, path, "takeover_no_library", null));
                    continue;
                }

                var suggested = candidates.Length == 1 ? candidates[0]
                    : candidates.FirstOrDefault(library => LastName(library.RelativePath) == LastName(folder.Path) || library.Name.Equals(LastName(folder.Path), StringComparison.OrdinalIgnoreCase));
                var isChosen = chosen.TryGetValue(Folder(folder.Path), out var chosenId);
                var choice = isChosen ? chosenId : suggested?.Id;
                items.AddRange(candidates.Select(library => new TakeoverItemV1(TakeoverItemType.RootFolder, path,
                    library.Id != choice ? "takeover_possible" : isChosen ? "takeover_assigned" : "takeover_suggested", null)
                {
                    LibraryRootId = library.Id,
                    LibraryName = library.Name
                }));
            }

            // Watched titles without files become wanted through their provider id; large libraries continue in later runs.
            var taken = 0;
            foreach (var title in titles.Where(title => title.IsMonitored && title.IsMissing))
            {
                var name = Limit(title.Year is { } year ? title.Title + " (" + year + ")" : title.Title, 500);

                // Lidarr's metadata profile may leave out an album's type.
                if (title.MetadataProfileId is { } metadataId && metadataById.TryGetValue(metadataId, out var metadata)
                    && !ServarrProfileLogic.Monitoring(metadata).AllowsType(title.AlbumType))
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_not_monitored", null));
                    continue;
                }

                var identity = mediaType switch
                {
                    MediaType.Movie or MediaType.Series => title.TmdbId?.ToString(CultureInfo.InvariantCulture) is { } tmdbId ? (mediaType == MediaType.Movie ? "tmdb_movie" : "tmdb_tv", tmdbId) : default,
                    MediaType.Music => title.ExternalId is { } album ? ("musicbrainz", album) : default,
                    _ => ("openlibrary", "")
                };
                if (identity.Item1 is null)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_no_id", null));
                }
                else if (!Data.Apply)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_will_want", null));
                }
                else if (taken++ >= TitlesPerRun)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_next_run", null));
                }
                else if ((mediaType == MediaType.Book ? await FindBookAsync(title) : identity.Item2) is not { } externalId)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_no_match", null));
                }
                else
                {
                    var workId = await _catalog.ImportAsync(Context, mediaType, null, identity.Item1, externalId, CancellationToken);
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.EnsureWantedItemAsync(new WantedTakeoverData(workId, title.QualityProfileId is { } profileId && profileIds.TryGetValue(profileId, out var id) ? id : null,
                            rootFolders.FirstOrDefault(folder => title.Path is { } titlePath && Contains(folder.Path, titlePath)) is { } root
                                && chosen.TryGetValue(Folder(root.Path), out var libraryRootId) ? libraryRootId : null), ct);
                        return true;
                    }, CancellationToken);
                    items.Add(new TakeoverItemV1(type, name, "takeover_wanted", null));
                }
            }

            return new BaseDataList<TakeoverItemV1>([.. items.Take(1000)]);
        }

        private async Task<TakeoverItemV1> TakeConnectionAsync(TakeoverItemType type, string name, string baseUrl, bool needsKey, HashSet<string> existing, SaveAcquisitionConnectionDataV1 data)
        {
            if (existing.Contains(baseUrl.TrimEnd('/')))
            {
                return new TakeoverItemV1(type, Limit(name, 500), "takeover_exists", null);
            }

            if (needsKey)
            {
                return new TakeoverItemV1(type, Limit(name, 500), "takeover_needs_key", baseUrl);
            }

            if (!Data.Apply)
            {
                return new TakeoverItemV1(type, Limit(name, 500), "takeover_will_add", null);
            }

            await Context.ExecuteBatchAsync((ctx, ct) => ctx.CreateAcquisitionConnectionAsync(AcquisitionConnectionInput.Create(null, data, _protector), ct), CancellationToken);
            existing.Add(baseUrl.TrimEnd('/'));
            return new TakeoverItemV1(type, Limit(name, 500), "takeover_added", null);
        }

        // The one Open Library work of that title, author and year; anything less clear is left for a person.
        private async Task<string?> FindBookAsync(ServarrTitle title)
        {
            var found = (await _catalog.SearchAsync(Context, MediaType.Book, null, title.Title, title.Year, CancellationToken))
                .Where(result => result.Namespace == BookMetadata.Namespace && TitleMatcher.Matches(title.Title, result.Title)
                    && (title.Year is null || result.ReleaseYear is null || Math.Abs(title.Year.Value - result.ReleaseYear.Value) <= 1)
                    && (title.Creator is null || result.Author?.Contains(title.Creator, StringComparison.OrdinalIgnoreCase) == true))
                .ToArray();
            return found.Length == 1 ? found[0].ExternalId : null;
        }

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

        // "12:/data/music" chooses library 12 for that root folder.
        private static (long LibraryRootId, string Path)? Choice(string value) =>
            value.IndexOf(':') is > 0 and var colon && colon < value.Length - 1 && long.TryParse(value.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? (id, value[(colon + 1)..])
                : null;

        private static string Folder(string path) => path.TrimEnd('/', '\\');

        private static string LastName(string path) => Folder(path).Split('/', '\\')[^1].ToLowerInvariant();

        // A title's folder lies in the root folder or is it.
        private static bool Contains(string folder, string path) => path.StartsWith(Folder(folder), StringComparison.Ordinal)
            && (path.Length == Folder(folder).Length || path[Folder(folder).Length] is '/' or '\\');
    }
}
