using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Media.V1;
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
            IReadOnlyList<ServarrTag> sourceTags = [];
            IReadOnlyList<ServarrReleaseProfile> releaseProfiles = [];
            IReadOnlyList<ServarrQualitySize> qualitySizes = [];
            try
            {
                if (Data.TakeoverSourceTypeId != TakeoverSourceType.Prowlarr)
                {
                    profiles = await _servarr.ReadQualityProfilesAsync(source, CancellationToken);
                    if (Data.TakeoverSourceTypeId is TakeoverSourceType.Sonarr or TakeoverSourceType.Radarr)
                    {
                        formats = await _servarr.ReadCustomFormatsAsync(source, CancellationToken);
                        formatCatalog = await _servarr.ReadFormatCatalogAsync(source, CancellationToken);
                        sourceTags = await _servarr.ReadTagsAsync(source, CancellationToken);
                        releaseProfiles = await _servarr.ReadReleaseProfilesAsync(source, CancellationToken);
                        qualitySizes = await _servarr.ReadQualitySizesAsync(source, CancellationToken);
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
                throw new ServiceUnavailableException(exception.Failure == ServarrFailure.Unauthorized ? "takeover_authentication_failed" : "takeover_unreachable");
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
                        profile."Name"
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

            // The custom formats found, besides the ones listed above as not taken over; the quality profiles bring the others along.
            var notTaken = items.Where(item => item.TakeoverItemTypeId == TakeoverItemType.Format).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            items.AddRange(formats.Where(format => !notTaken.Contains(Limit(format.Name, 500))).Select(format => new TakeoverItemV1(TakeoverItemType.Format, Limit(format.Name, 500), "takeover_found", null)));

            // Sonarr's and Radarr's tags, release profiles, quality sizes and the release groups their custom formats rate.
            var tagIds = await TakeTagsAsync(sourceTags, items);
            await TakeReleaseProfilesAsync(releaseProfiles, mediaType, tagIds, items);
            await TakeQualitySizesAsync(qualitySizes, mediaType, items);
            await TakeReleaseGroupsAsync(formats, profiles, items);

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

            // Movies and series come over whole, the missing monitored titles of every other media type; every title taken over belongs to the library at once,
            // monitored as Sonarr, Radarr or their kin monitored it. Previously imported provider ids never use the limited catalog-import budget.
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

                // A series is TheTVDB's, by Sonarr's own id; TMDB's id leads there too, and without either the title and year must name one series.
                var matchedSeries = mediaType == MediaType.Series && title.TvdbId is null && title.TmdbId is null ? await FindSeriesAsync(title) : null;
                var identity = mediaType switch
                {
                    MediaType.Movie => title.TmdbId is { } movieId ? ("tmdb_movie", movieId.ToString(CultureInfo.InvariantCulture)) : default,
                    MediaType.Series => title.TvdbId is { } tvdbId ? ("tvdb", tvdbId.ToString(CultureInfo.InvariantCulture))
                        : title.TmdbId is { } seriesId ? ("tmdb_tv", seriesId.ToString(CultureInfo.InvariantCulture))
                        : matchedSeries is { } found ? ("tvdb", found) : default,
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

                // A series may already be a Work by any of Sonarr's ids.
                var titleIds = new List<object> { new { Namespace = identity.Item1, Value = externalId } };
                if (mediaType == MediaType.Series)
                {
                    if (title.TvdbId is { } tvdb) titleIds.Add(new { Namespace = "tvdb", Value = tvdb.ToString(CultureInfo.InvariantCulture) });
                    if (title.TmdbId is { } tmdb) titleIds.Add(new { Namespace = "tmdb_tv", Value = tmdb.ToString(CultureInfo.InvariantCulture) });
                    if (title.ImdbId is { } imdb) titleIds.Add(new { Namespace = "imdb", Value = imdb });
                }

                var existingWork = await Context.ExecuteBatchAsync((ctx, ct) =>
                {
                    var sql = new SqlStatement();
                    sql.Statement =
                        """
                        SELECT
                            identity."WorkId" AS "Id"
                        FROM
                            "WorkExternalIdentities" AS identity
                            JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value"
                        ORDER BY
                            identity."Namespace" = @Namespace DESC,
                            identity."WorkId"
                        LIMIT 1;
                        """;
                    sql.Parameters.Add(new WorkIdentityLookupData(JsonSerializer.Serialize(titleIds)));
                    sql.Parameters.Add(new WorkIdentityProbeData(identity.Item1, externalId));
                    return ctx.ReadSql.ReadOptionalAsync<WorkIdData>(sql, ct);
                }, CancellationToken);
                if (existingWork is null && taken >= TitlesPerRun)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_next_run", null));
                    continue;
                }

                long workId;
                try
                {
                    workId = existingWork?.Id ?? await _catalog.ImportAsync(Context, mediaType, null, identity.Item1, externalId, CancellationToken);
                }
                catch (ServiceUnavailableException) when (mediaType == MediaType.Series)
                {
                    // Without TheTVDB (no key yet, or out of reach) the series comes over with Sonarr's ids, title and year; its details follow once TheTVDB answers.
                    if (title.TvdbId is not { } tvdbId)
                    {
                        items.Add(new TakeoverItemV1(type, name, "takeover_next_run", null));
                        continue;
                    }

                    var series = new ExternalWorkData("tvdb", tvdbId.ToString(CultureInfo.InvariantCulture), Limit(title.Title, 500), null, title.Year is >= 1800 and <= 2200 ? title.Year : null,
                        null, null, null, null, null) { TmdbValue = title.TmdbId?.ToString(CultureInfo.InvariantCulture), ImdbValue = title.ImdbId };
                    workId = (await MetadataCatalog.KeepWorksAsync(Context, new ExternalWorkListData("tvdb", MediaType.Series, null), [series], CancellationToken)).Single().WorkId!.Value;
                }

                if (existingWork is null)
                {
                    taken++;
                }

                // A monitored title is wanted and searched, an unmonitored one is wanted paused; a target the title already has stays as it is.
                var titleTags = title.TagIds.Where(tagIds.ContainsKey).Select(id => tagIds[id]).Distinct().ToArray();
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    if (titleTags.Length > 0) await ctx.TagTakenOverWorkAsync(new TakeoverWorkTagsData(workId, titleTags), ct);
                    await ctx.EnsureWantedItemAsync(new WantedTakeoverData(workId, sourceProfileId is { } profileId && profileIds.TryGetValue((profileId, sourceMetadataId), out var id) ? id : null,
                        SourceRoot(title) is { } root && chosen.TryGetValue(Folder(root.Path), out var libraryRootId) ? libraryRootId : null, !title.IsMonitored), ct);
                    return true;
                }, CancellationToken);

                items.Add(new TakeoverItemV1(type, name, existingWork is not null ? "takeover_exists" : wantsDownload ? "takeover_wanted" : "takeover_added", null));
            }

            return new BaseDataList<TakeoverItemV1>([.. items.Take(1000)]);

            ServarrRootFolder? SourceRoot(ServarrTitle title) => title.Path is { } path
                ? rootFolders.OrderByDescending(folder => Folder(folder.Path).Length).FirstOrDefault(folder => Contains(folder.Path, path)) : null;
            int? SourceProfile(ServarrTitle title) => title.QualityProfileId ?? SourceRoot(title)?.DefaultQualityProfileId;
            int SourceMetadata(ServarrTitle title) => title.MetadataProfileId ?? SourceRoot(title)?.DefaultMetadataProfileId ?? 0;
        }

        // A tag comes over under its label in Jularr's form (small letters, digits, spaces, dashes); one of that label already is the same tag.
        private async Task<Dictionary<int, long>> TakeTagsAsync(IReadOnlyList<ServarrTag> tags, List<TakeoverItemV1> items)
        {
            var map = new Dictionary<int, long>();
            if (tags.Count == 0) return map;
            var known = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement = """SELECT tag."Id", tag."Label", tag."Version" FROM "Tags" AS tag;""";
                return ctx.ReadSql.ReadListAsync<AcquisitionTagRowData>(sql, ct);
            }, CancellationToken);
            var byLabel = known.GroupBy(tag => tag.Label, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                var label = TagLabel(tag.Label);
                if (label is null) continue;
                if (byLabel.TryGetValue(label, out var id))
                {
                    map[tag.Id] = id;
                    items.Add(new TakeoverItemV1(TakeoverItemType.Tag, label, "takeover_exists", null));
                    continue;
                }

                if (!Data.Apply)
                {
                    items.Add(new TakeoverItemV1(TakeoverItemType.Tag, label, "takeover_will_add", null));
                    continue;
                }

                var key = await Context.ExecuteBatchAsync((ctx, ct) => ctx.SaveAcquisitionTagAsync(new AcquisitionTagWriteData(null, label, null), ct), CancellationToken);
                byLabel[label] = map[tag.Id] = key.Id;
                items.Add(new TakeoverItemV1(TakeoverItemType.Tag, label, "takeover_added", null));
            }

            return map;
        }

        // "Anime HD!" is "anime hd"; a label with nothing left over is not taken over.
        private static string? TagLabel(string label)
        {
            var cleaned = new string([.. label.Trim().ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '_' or '-' ? character : '-')]).Trim(' ', '-', '_');
            cleaned = cleaned.Length > 64 ? cleaned[..64].TrimEnd(' ', '-', '_') : cleaned;
            return cleaned.Length > 0 && char.IsAsciiLetterOrDigit(cleaned[0]) ? cleaned : null;
        }

        // A release profile comes over for the media type of the source, with its terms and tags; one of that name already is kept as it is.
        private async Task TakeReleaseProfilesAsync(IReadOnlyList<ServarrReleaseProfile> profiles, MediaType mediaType, Dictionary<int, long> tagIds, List<TakeoverItemV1> items)
        {
            if (profiles.Count == 0) return;
            var known = (await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement = """SELECT release."Id", release."Name" FROM "ReleaseProfiles" AS release;""";
                return ctx.ReadSql.ReadListAsync<AcquisitionProfileNameData>(sql, ct);
            }, CancellationToken)).Select(release => release.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < profiles.Count; index++)
            {
                var profile = profiles[index];
                var name = Limit(profile.Name.Length > 0 ? profile.Name : Data.TakeoverSourceTypeId + " " + (index + 1).ToString(CultureInfo.InvariantCulture), 128);
                if (known.Contains(name))
                {
                    items.Add(new TakeoverItemV1(TakeoverItemType.ReleaseProfile, name, "takeover_exists", null));
                    continue;
                }

                if (!Data.Apply)
                {
                    items.Add(new TakeoverItemV1(TakeoverItemType.ReleaseProfile, name, "takeover_will_add", null));
                    continue;
                }

                var tags = profile.TagIds.Where(tagIds.ContainsKey).Select(id => tagIds[id]).Distinct().ToArray();
                try
                {
                    await Context.ExecuteBatchAsync((ctx, ct) => ctx.SaveReleaseProfileAsync(new ReleaseProfileWriteData(null, mediaType, name, profile.IsEnabled, profile.Required, profile.Ignored,
                        null, tags, null), ct), CancellationToken);
                    known.Add(name);
                    items.Add(new TakeoverItemV1(TakeoverItemType.ReleaseProfile, name, "takeover_added", null));
                }
                catch (DataValidationException)
                {
                    items.Add(new TakeoverItemV1(TakeoverItemType.ReleaseProfile, name, "takeover_release_skipped", null));
                }
            }
        }

        // A quality's sizes are compared with Jularr's: alike ones need nothing, different ones are taken over only where the person chose them in the preview.
        private async Task TakeQualitySizesAsync(IReadOnlyList<ServarrQualitySize> sizes, MediaType mediaType, List<TakeoverItemV1> items)
        {
            if (sizes.Count == 0 || mediaType is not (MediaType.Movie or MediaType.Series)) return;
            var current = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT quality."Quality", quality."MinimumMbPerMinute", quality."PreferredMbPerMinute", quality."MaximumMbPerMinute", quality."Version"
                    FROM "QualityDefinitions" AS quality
                    WHERE quality."MediaTypeId" = @MediaTypeId
                    ORDER BY quality."Quality";
                    """;
                sql.Parameters.Add(new QualityDefinitionsParametersV1(mediaType));
                return ctx.ReadSql.ReadListAsync<QualityDefinitionV1>(sql, ct);
            }, CancellationToken);
            var chosen = (Data.OverwriteSizes ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = current.ToDictionary(row => row.Quality, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var size in sizes)
            {
                if (!rows.TryGetValue(size.Quality, out var row)) continue;
                var minimum = Math.Clamp(Math.Round(size.Minimum, 2), 0, 999999m);
                var maximum = size.Maximum is { } top ? Math.Clamp(Math.Round(top, 2), minimum, 999999m) : (decimal?)null;
                var preferred = size.Preferred is { } middle ? Math.Clamp(Math.Round(middle, 2), minimum, maximum ?? 999999m) : (decimal?)null;
                var label = Limit(row.Quality + ": " + Sizes(minimum, preferred, maximum), 500);
                if (row.MinimumMbPerMinute == minimum && row.PreferredMbPerMinute == preferred && row.MaximumMbPerMinute == maximum)
                {
                    items.Add(new TakeoverItemV1(TakeoverItemType.QualitySize, label, "takeover_size_same", null));
                    continue;
                }

                if (!Data.Apply || !chosen.Contains(row.Quality))
                {
                    items.Add(new TakeoverItemV1(TakeoverItemType.QualitySize, label, "takeover_size_differs", null));
                    continue;
                }

                rows[row.Quality] = new QualityDefinitionV1(row.Quality, minimum, preferred, maximum, row.Version);
                changed = true;
                items.Add(new TakeoverItemV1(TakeoverItemType.QualitySize, label, "takeover_added", null));
            }

            if (changed)
            {
                var json = JsonSerializer.Serialize(rows.Values.Select(row => new { row.Quality, row.MinimumMbPerMinute, row.PreferredMbPerMinute, row.MaximumMbPerMinute, row.Version }));
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.SaveQualityDefinitionsAsync(new QualityDefinitionsWriteData(mediaType, json), ct);
                    return true;
                }, CancellationToken);
            }

            static string Sizes(decimal minimum, decimal? preferred, decimal? maximum) =>
                minimum.ToString("0.##", CultureInfo.InvariantCulture) + " / " + (preferred?.ToString("0.##", CultureInfo.InvariantCulture) ?? "–") + " / "
                + (maximum?.ToString("0.##", CultureInfo.InvariantCulture) ?? "∞") + " MB/min";
        }

        // A custom format that names release groups rates them by its points in the profiles: plus is good, minus bad, -10000 or less blocked. A group Jularr has rated keeps its rating.
        private async Task TakeReleaseGroupsAsync(IReadOnlyList<ServarrCustomFormat> formats, IReadOnlyList<ServarrQualityProfile> profiles, List<TakeoverItemV1> items)
        {
            var ratings = new Dictionary<string, ReleaseGroupRating>(StringComparer.OrdinalIgnoreCase);
            foreach (var format in formats)
            {
                var conditions = format.Conditions.Where(condition => condition.Implementation == "ReleaseGroupSpecification" && !condition.Negate).ToArray();
                if (conditions.Length == 0) continue;
                var scores = profiles.Select(profile => profile.FormatScores.TryGetValue(format.Id, out var score) ? score : 0).Where(score => score != 0).ToArray();
                if (scores.Length == 0) continue;
                var score = scores.MaxBy(Math.Abs);
                var rating = score <= -10000 ? ReleaseGroupRating.Blocked : score < 0 ? ReleaseGroupRating.Bad : ReleaseGroupRating.Good;
                foreach (var name in conditions.SelectMany(condition => GroupNames(condition.Fields.TryGetValue("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)))
                {
                    if (!ratings.TryGetValue(name, out var known) || rating > known) ratings[name] = rating;
                }
            }

            if (ratings.Count == 0) return;
            var rated = (await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT item."Id", item."Name", item."ReleaseGroupRatingId", item."SeenCount", item."LastSeenAt", '{}'::smallint[] AS "MediaKinds"
                    FROM "ReleaseGroups" AS item
                    WHERE item."ReleaseGroupRatingId" IS NOT NULL;
                    """;
                return ctx.ReadSql.ReadListAsync<ReleaseGroupDetailsV1>(sql, ct);
            }, CancellationToken)).ToDictionary(group => group.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, rating) in ratings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(500))
            {
                if (rated.ContainsKey(name))
                {
                    items.Add(new TakeoverItemV1(TakeoverItemType.ReleaseGroup, name, "takeover_exists", null));
                    continue;
                }

                if (Data.Apply)
                {
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        var key = await ctx.AddReleaseGroupAsync(new ReleaseGroupAddData(name), ct);
                        await ctx.RateReleaseGroupAsync(new ReleaseGroupRatingData(key.Id, rating), ct);
                        return true;
                    }, CancellationToken);
                }

                items.Add(new TakeoverItemV1(TakeoverItemType.ReleaseGroup, name, "takeover_group_" + rating.ToString().ToLowerInvariant(), null));
            }
        }

        // "^(FLUX|NTb)$", "\b(FLUX)\b" or "(?i)^flux$" name groups; a pattern that needs more than names and bars names none.
        private static IEnumerable<string> GroupNames(string? pattern)
        {
            if (pattern is null) return [];
            var core = pattern.Replace("(?i)", "", StringComparison.Ordinal).Replace("\\b", "", StringComparison.Ordinal).Trim('^', '$', ' ');
            if (core.StartsWith('(') && core.EndsWith(')')) core = core[1..^1];
            var names = core.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return names.All(name => name.Length <= 64 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
                ? names.Select(name => name.Replace("\\.", ".", StringComparison.Ordinal)) : [];
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

        // The one TheTVDB series of that title and year; none while TheTVDB cannot search.
        private async Task<string?> FindSeriesAsync(ServarrTitle title)
        {
            IReadOnlyList<MetadataSearchResultV1> results;
            try
            {
                results = await _catalog.SearchAsync(Context, MediaType.Series, null, title.Title, title.Year, CancellationToken);
            }
            catch (ServiceUnavailableException)
            {
                return null;
            }

            var found = results.Where(result => result.Namespace == "tvdb" && TitleMatcher.Matches(title.Title, result.Title) && title.Year is { } year && result.ReleaseYear == year).ToArray();
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
