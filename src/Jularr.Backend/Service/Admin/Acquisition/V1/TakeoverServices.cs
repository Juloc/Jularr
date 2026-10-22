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
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Logic.Library;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    // A one-time takeover from Prowlarr or a Servarr: indexers, clients, quality profiles with their custom formats and monitoring, root folders and existing titles; nothing keeps running alongside.
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
            ServarrFormatCatalog? formatCatalog = null;
            try
            {
                if (Data.TakeoverSourceTypeId != TakeoverSourceType.Prowlarr)
                {
                    profiles = await _servarr.ReadQualityProfilesAsync(source, CancellationToken);
                    if (Data.TakeoverSourceTypeId is TakeoverSourceType.Sonarr or TakeoverSourceType.Radarr)
                    {
                        formats = await _servarr.ReadCustomFormatsAsync(source, CancellationToken);
                        formatCatalog = await _servarr.ReadFormatCatalogAsync(source, CancellationToken);
                    }
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
                        "AcquisitionConnections" AS connection
                    WHERE
                        connection."BaseUrl" IS NOT NULL;
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
                        root."RelativePath",
                        root."StorageMountId"
                    FROM
                        "LibraryRoots" AS root
                    ORDER BY
                        root."Id";
                    """;
                return (await ctx.ReadSql.ReadListAsync<AcquisitionConnectionUrlData>(sql, ct), await ctx.ReadSql.ReadListAsync<AcquisitionProfileNameData>(profileSql, ct),
                    await ctx.ReadSql.ReadListAsync<TakeoverLibraryData>(librarySql, ct));
            }, CancellationToken);
            var existing = known.Select(connection => connection.BaseUrl.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var suppliedKeys = Data.ConnectionKeys.Where(item => !string.IsNullOrWhiteSpace(item.ApiKey)).ToArray();
            if (suppliedKeys.Any(key => key.TakeoverItemTypeId switch
                {
                    TakeoverItemType.Indexer => !indexers.Any(item => SameAddress(item.BaseUrl, key.BaseUrl)),
                    TakeoverItemType.Client => !clients.Any(item => SameAddress(item.BaseUrl, key.BaseUrl)),
                    _ => true
                })) throw new DataValidationException(nameof(Data.ConnectionKeys), "unknown_reference");
            if (suppliedKeys.GroupBy(key => (key.TakeoverItemTypeId, key.BaseUrl.TrimEnd('/').ToLowerInvariant())).Any(group => group.Count() > 1))
                throw new DataValidationException(nameof(Data.ConnectionKeys), "duplicate_value");

            // Connections whose key the source hides are completed in the connection form; open indexers come as they are.
            var items = new List<TakeoverItemV1>();
            foreach (var indexer in indexers)
            {
                var key = suppliedKeys.FirstOrDefault(item => item.TakeoverItemTypeId == TakeoverItemType.Indexer && SameAddress(item.BaseUrl, indexer.BaseUrl))?.ApiKey ?? indexer.ApiKey;
                items.Add(await TakeConnectionAsync(TakeoverItemType.Indexer, indexer.Name, indexer.BaseUrl, key is null && indexer.HasHiddenKey, existing,
                    new SaveAcquisitionConnectionDataV1(Limit(indexer.Name, 128), AcquisitionConnectionType.Newznab, indexer.BaseUrl, key, indexer.IsEnabled, indexer.Priority,
                        indexer.Categories, null, null, null)));
            }

            foreach (var client in clients)
            {
                var key = suppliedKeys.FirstOrDefault(item => item.TakeoverItemTypeId == TakeoverItemType.Client && SameAddress(item.BaseUrl, client.BaseUrl))?.ApiKey ?? client.ApiKey;
                items.Add(await TakeConnectionAsync(TakeoverItemType.Client, client.Name, client.BaseUrl, key is null, existing,
                    new SaveAcquisitionConnectionDataV1(Limit(client.Name, 128), AcquisitionConnectionType.Sabnzbd, client.BaseUrl, key, false, client.Priority, [],
                        client.Category is { Length: <= 64 } category ? category : null, null, null)));
            }

            var (type, mediaType) = Data.TakeoverSourceTypeId switch
            {
                TakeoverSourceType.Radarr => (TakeoverItemType.Movie, MediaType.Movie),
                TakeoverSourceType.Sonarr => (TakeoverItemType.Series, MediaType.Series),
                TakeoverSourceType.Lidarr => (TakeoverItemType.Album, MediaType.Music),
                _ => (TakeoverItemType.Book, MediaType.Book)
            };

            // Each quality/metadata combination keeps its own monitoring policy and source identity.
            var profileIds = new Dictionary<(int Quality, int Metadata), long>();
            var supportedProfiles = new HashSet<(int Quality, int Metadata)>();
            var formatsById = formats.ToDictionary(format => format.Id);
            var metadataById = metadataProfiles.ToDictionary(metadata => metadata.Id);
            foreach (var profile in profiles)
            {
                var metadataIds = titles.Where(title => SourceProfile(title) == profile.Id).Select(SourceMetadata)
                    .Concat(rootFolders.Where(folder => folder.DefaultQualityProfileId == profile.Id).Select(folder => folder.DefaultMetadataProfileId ?? 0))
                    .DefaultIfEmpty(0).Distinct().Order().ToArray();
                foreach (var metadataId in metadataIds)
                {
                    var metadata = metadataById.GetValueOrDefault(metadataId);
                    var name = Limit(profile.Name + (metadataIds.Length > 1 && metadata is not null ? " (" + metadata.Name + ")" : ""), 128);
                    if (metadataId != 0 && metadata is null)
                    {
                        items.Add(new TakeoverItemV1(TakeoverItemType.Profile, name, "takeover_profile_unsupported", null));
                        continue;
                    }
                    var mapped = ServarrProfileLogic.Map(profile, formatsById, mediaType, metadata, formatCatalog, name);
                    var outcome = await Context.ExecuteBatchAsync((ctx, ct) => ctx.TakeOverProfileAsync(mapped,
                        new ProfileImportReferenceData(Data.BaseUrl.TrimEnd('/'), profile.Id, mediaType, metadataId), jularrProfiles, Data.Apply, ct), CancellationToken);
                    if (outcome.ProfileId is { } profileId) profileIds[(profile.Id, metadataId)] = profileId;
                    if (outcome.OutcomeKey is "takeover_added" or "takeover_will_add" or "takeover_exists") supportedProfiles.Add((profile.Id, metadataId));
                    items.Add(new TakeoverItemV1(TakeoverItemType.Profile, name, outcome.OutcomeKey, null));
                    items.AddRange(mapped.SkippedFormats.Select(format => new TakeoverItemV1(TakeoverItemType.Format, Limit(format, 500), "takeover_format_skipped", null)));
                }
            }

            // Each root folder goes to a chosen library of its type. A library named like the folder, or the only one, is suggested.
            var chosen = (Data.RootFolders ?? []).Select(Choice).OfType<(long LibraryRootId, string Path)>().GroupBy(choice => Folder(choice.Path), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().LibraryRootId, StringComparer.Ordinal);
            var candidates = libraries.Where(library => library.MediaTypeId == mediaType).ToList();
            var newLibraries = Data.NewLibraries.Where(item => item.StorageMountId is not null).ToArray();
            if (newLibraries.Select(item => Folder(item.SourcePath)).Distinct(StringComparer.Ordinal).Count() != newLibraries.Length)
                throw new DataValidationException(nameof(Data.NewLibraries), "duplicate_value");
            foreach (var library in newLibraries)
            {
                if (!rootFolders.Any(folder => Folder(folder.Path) == Folder(library.SourcePath)))
                    throw new DataValidationException(nameof(Data.NewLibraries), "unknown_reference");
                var input = new LibraryRootCreationData(library.Name, mediaType, null, library.StorageMountId!.Value, library.RelativePath);
                input.Validate();
                var found = candidates.FirstOrDefault(item => item.StorageMountId == input.StorageMountId && item.RelativePath == input.RelativePath);
                if (found is not null) chosen[Folder(library.SourcePath)] = found.Id;
                else if (Data.Apply)
                {
                    var id = await Context.ExecuteBatchAsync((ctx, ct) => ctx.CreateLibraryRootAsync(input, ct), CancellationToken);
                    candidates.Add(new TakeoverLibraryData(id, input.Name, mediaType, input.RelativePath, input.StorageMountId));
                    chosen[Folder(library.SourcePath)] = id;
                }
                else items.Add(new TakeoverItemV1(TakeoverItemType.RootFolder, library.SourcePath, "takeover_will_add", null) { LibraryName = library.Name });
            }
            foreach (var folder in rootFolders)
            {
                var path = Limit(folder.Path, 500);
                if (candidates.Count == 0)
                {
                    items.Add(new TakeoverItemV1(TakeoverItemType.RootFolder, path, "takeover_no_library", null));
                    continue;
                }

                var suggested = candidates.Count == 1 ? candidates[0]
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

            // Import existing movies/series without starting downloads; missing monitored titles of every media type become wanted.
            // Previously imported provider ids never use the limited catalog-import budget.
            var taken = 0;
            foreach (var title in titles.Where(item => item.IsMonitored && item.IsMissing || mediaType is MediaType.Movie or MediaType.Series)
                .OrderByDescending(item => item.IsMonitored && item.IsMissing))
            {
                var wantsDownload = title.IsMonitored && title.IsMissing;
                var name = Limit(title.Year is { } year ? title.Title + " (" + year + ")" : title.Title, 500);
                var sourceProfileId = SourceProfile(title);
                var sourceMetadataId = SourceMetadata(title);
                if (wantsDownload && sourceProfileId is { } requiredProfile && !supportedProfiles.Contains((requiredProfile, sourceMetadataId)))
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_profile_unsupported", null));
                    continue;
                }

                if (wantsDownload && metadataById.TryGetValue(sourceMetadataId, out var metadata)
                    && !ServarrProfileLogic.Monitoring(metadata).AllowsType(title.AlbumType))
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_not_monitored", null));
                    continue;
                }

                var matchedSeries = mediaType == MediaType.Series && title.TmdbId is null ? await FindSeriesAsync(title) : null;
                var identity = mediaType switch
                {
                    MediaType.Movie or MediaType.Series => (title.TmdbId?.ToString(CultureInfo.InvariantCulture) ?? matchedSeries) is { } tmdbId ? (mediaType == MediaType.Movie ? "tmdb_movie" : "tmdb_tv", tmdbId) : default,
                    MediaType.Music => title.ExternalId is { } album ? ("musicbrainz", album) : default,
                    _ => ("openlibrary", "")
                };
                if (identity.Item1 is null)
                {
                    items.Add(new TakeoverItemV1(type, name, mediaType == MediaType.Series ? "takeover_no_match" : "takeover_no_id", null));
                    continue;
                }

                if (!Data.Apply)
                {
                    items.Add(new TakeoverItemV1(type, name, wantsDownload ? "takeover_will_want" : "takeover_will_add", null));
                    continue;
                }

                if ((mediaType == MediaType.Book ? await FindBookAsync(title) : identity.Item2) is not { } externalId)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_no_match", null));
                    continue;
                }

                var existingWork = await Context.ExecuteBatchAsync((ctx, ct) =>
                {
                    var sql = new SqlStatement();
                    sql.Statement =
                        """
                        SELECT identity."WorkId" AS "Id"
                        FROM "WorkExternalIdentities" AS identity
                        WHERE identity."Namespace" = @Namespace AND identity."Value" = @Value;
                        """;
                    sql.Parameters.Add(new WorkIdentityProbeData(identity.Item1, externalId));
                    return ctx.ReadSql.ReadOptionalAsync<WorkIdData>(sql, ct);
                }, CancellationToken);
                if (existingWork is null && taken >= TitlesPerRun)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_next_run", null));
                    continue;
                }

                var workId = existingWork?.Id ?? await _catalog.ImportAsync(Context, mediaType, null, identity.Item1, externalId, CancellationToken);
                if (existingWork is null)
                {
                    taken++;
                }

                if (wantsDownload)
                {
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.EnsureWantedItemAsync(new WantedTakeoverData(workId, sourceProfileId is { } profileId && profileIds.TryGetValue((profileId, sourceMetadataId), out var id) ? id : null,
                            SourceRoot(title) is { } root
                                && chosen.TryGetValue(Folder(root.Path), out var libraryRootId) ? libraryRootId : null), ct);
                        return true;
                    }, CancellationToken);
                }

                items.Add(new TakeoverItemV1(type, name, existingWork is not null ? "takeover_exists" : wantsDownload ? "takeover_wanted" : "takeover_added", null));
            }

            return new BaseDataList<TakeoverItemV1>([.. items.Take(1000)]);

            ServarrRootFolder? SourceRoot(ServarrTitle title) => title.Path is { } path
                ? rootFolders.OrderByDescending(folder => Folder(folder.Path).Length).FirstOrDefault(folder => Contains(folder.Path, path)) : null;
            int? SourceProfile(ServarrTitle title) => title.QualityProfileId ?? SourceRoot(title)?.DefaultQualityProfileId;
            int SourceMetadata(ServarrTitle title) => title.MetadataProfileId ?? SourceRoot(title)?.DefaultMetadataProfileId ?? 0;
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

        private async Task<string?> FindSeriesAsync(ServarrTitle title)
        {
            var found = (await _catalog.SearchAsync(Context, MediaType.Series, null, title.Title, title.Year, CancellationToken))
                .Where(result => result.Namespace == "tmdb_tv" && TitleMatcher.Matches(title.Title, result.Title)
                    && title.Year is { } year && result.ReleaseYear == year).ToArray();
            return found.Length == 1 ? found[0].ExternalId : null;
        }

        private static bool SameAddress(string left, string right) => left.TrimEnd('/').Equals(right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

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
