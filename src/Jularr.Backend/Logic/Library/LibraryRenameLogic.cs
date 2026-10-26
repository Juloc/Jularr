using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    // Moves identified files to the import layout; nothing moves before the Owner confirms the exact plan.
    internal static partial class LibraryRenameLogic
    {
        public static Task<IReadOnlyList<LibraryRenameFileData>> ReadLibraryRenameFilesAsync(this LogicContext ctx, CancellationToken ct)
        {
            // Every known path counts as taken; a library being scanned keeps its names.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH numbered_episodes AS (
                    SELECT episode."Id", row_number() OVER (PARTITION BY episode."WorkId" ORDER BY season."Number", episode."Number")::numeric AS "AbsoluteNumber"
                    FROM "WorkUnits" AS episode
                    JOIN "WorkUnits" AS season ON season."Id" = episode."ParentWorkUnitId"
                    WHERE episode."WorkUnitTypeId" = 2 AND season."Number" > 0
                )
                SELECT
                    file."Id",
                    work."Id" AS "WorkId",
                    file."LibraryRootId",
                    root."Name" AS "LibraryRootName",
                    root."MediaTypeId",
                    CASE WHEN class."WorkId" IS NOT NULL THEN class."MediaSubtypeId" ELSE coalesce(work."MediaSubtypeId", root."MediaSubtypeId") END AS "MediaSubtypeId",
                    file."RelativePath",
                    file."MissingSince",
                    file."MediaAssetId",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    (
                        SELECT
                            creator."Creator"
                        FROM
                            "WorkCreators" AS creator
                        WHERE
                            creator."WorkId" = work."Id"
                    ) AS "Author",
                    unit."WorkUnitTypeId",
                    numbering."AbsoluteNumber",
                    coalesce((SELECT jsonb_agg(numbered."AbsoluteNumber" ORDER BY covered."Number")::text
                        FROM "MediaAssetUnitCoverage" AS coverage
                        JOIN "WorkUnits" AS covered ON covered."Id" = coverage."WorkUnitId"
                        JOIN numbered_episodes AS numbered ON numbered."Id" = covered."Id"
                        WHERE coverage."MediaAssetId" = asset."Id"), '[]') AS "AbsoluteNumbers",
                    coalesce((SELECT jsonb_agg(covered."Number" ORDER BY covered."Number")::text
                        FROM "MediaAssetUnitCoverage" AS coverage
                        JOIN "WorkUnits" AS covered ON covered."Id" = coverage."WorkUnitId"
                        WHERE coverage."MediaAssetId" = asset."Id" AND covered."WorkUnitTypeId" = 2), '[]') AS "EpisodeNumbers",
                    unit."Number" AS "UnitNumber",
                    unit."Title" AS "UnitTitle",
                    unit."DiscNumber"::integer,
                    (SELECT max(track."DiscNumber")::integer FROM "WorkUnits" AS track WHERE track."WorkId" = work."Id" AND track."WorkUnitTypeId" = 5) AS "DiscCount",
                    coalesce(edition."LanguageCode", work."OriginalLanguage") AS "Language",
                    platform."Name" AS "Platform",
                    unit."ReleasedOn" AS "UnitReleasedOn",
                    parent."Number" AS "ParentUnitNumber",
                    file."QualityKey",
                    file."ReleaseGroup",
                    file."Edition",
                    file."Revision",
                    coalesce((SELECT jsonb_object_agg(identity."Namespace", identity."Value")::text FROM "WorkExternalIdentities" AS identity
                        WHERE identity."WorkId" = work."Id"), '{}') AS "ProviderIds",
                    coalesce((SELECT jsonb_agg(jsonb_build_object('MediaTrackTypeId', track."MediaTrackTypeId", 'Codec', track."Codec",
                        'Language', track."Language", 'Channels', track."Channels", 'BitDepth', track."BitDepth", 'DynamicRange', track."DynamicRange", 'Is3D', track."Is3D") ORDER BY track."StreamIndex")::text
                        FROM "MediaTracks" AS track WHERE track."StoredFileId" = file."Id"), '[]') AS "MediaTracks",
                    coalesce((SELECT jsonb_object_agg(format."Name", format."Name")::text
                        FROM "StoredFileCustomFormats" AS evaluated
                        JOIN "CustomFormats" AS format ON format."Id" = evaluated."CustomFormatId"
                        JOIN "StoredFileFormatEvidence" AS evidence ON evidence."StoredFileId" = evaluated."StoredFileId"
                        WHERE evaluated."StoredFileId" = file."Id" AND evaluated."Matches" AND evaluated."IsValid"
                            AND evaluated."FormatVersion" = format."Version" AND evaluated."Evidence" = evidence."Evidence"
                            AND format."IncludeWhenRenaming"), '{}') AS "CustomFormats",
                    file."ReleaseName",
                    franchise."Name" AS "Collection",
                    coalesce((
                        SELECT jsonb_object_agg(translation."Locale", translation."Title")::text
                        FROM (
                            SELECT DISTINCT ON (lower(split_part(locale."Locale", '-', 1)))
                                lower(split_part(locale."Locale", '-', 1)) AS "Locale",
                                title."Title"
                            FROM "WorkTitles" AS title
                            JOIN "UiLocales" AS locale ON locale."Id" = title."UiLocaleId"
                            WHERE title."WorkId" = work."Id"
                            ORDER BY lower(split_part(locale."Locale", '-', 1)),
                                title."IsManual" DESC, title."UpdatedAt" DESC, title."Id" DESC
                        ) AS translation
                    ), '{}') AS "LocalizedTitles",
                    (
                        SELECT
                            max(track."Width")
                        FROM
                            "MediaTracks" AS track
                        WHERE
                            track."StoredFileId" = file."Id"
                            AND track."MediaTrackTypeId" = 1
                    ) AS "Width"
                FROM
                    "StoredFiles" AS file
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    LEFT JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    LEFT JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    LEFT JOIN "WorkClasses" AS class ON class."WorkId" = work."Id"
                    LEFT JOIN "WorkVersions" AS version ON version."Id" = asset."WorkVersionId"
                    LEFT JOIN "WorkEditions" AS edition ON edition."Id" = version."WorkEditionId"
                    LEFT JOIN "GameReleases" AS game ON game."WorkVersionId" = version."Id"
                    LEFT JOIN "GamePlatforms" AS platform ON platform."Id" = game."GamePlatformId"
                    LEFT JOIN "Franchises" AS franchise ON franchise."Id" = work."FranchiseId"
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    LEFT JOIN numbered_episodes AS numbering ON numbering."Id" = unit."Id"
                WHERE
                    NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "Operations" AS operation
                        WHERE
                            operation."LibraryRootId" = root."Id"
                            AND operation."OperationTypeId" = 1
                            AND operation."OperationStatusTypeId" IN (1, 2)
                    )
                ORDER BY
                    file."LibraryRootId",
                    file."RelativePath";
                """;
            return ctx.ReadSql.ReadListAsync<LibraryRenameFileData>(sql, ct);
        }

        public static IReadOnlyList<LibraryRenameData> Plan(IReadOnlyList<LibraryRenameFileData> files, NamingSet patterns)
        {
            var renames = new List<LibraryRenameData>();
            foreach (var root in files.GroupBy(file => file.LibraryRootId))
            {
                // Shares often ignore case, so case alone never separates two names.
                var taken = new HashSet<string>(root.Select(file => file.RelativePath), StringComparer.OrdinalIgnoreCase);
                var present = root.Where(file => file.MissingSince is null).ToArray();
                var media = present.Where(file => file.MediaAssetId is not null && !LibraryFileTypes.IsSubtitle(file.RelativePath)).ToArray();

                // Subtitles follow the media file whose name they extend the most.
                var subtitles = present.Where(file => LibraryFileTypes.IsSubtitle(file.RelativePath))
                    .Select(file => (File: file, Owner: media.Where(item => file.RelativePath.StartsWith(Stem(item.RelativePath) + ".", StringComparison.Ordinal))
                        .MaxBy(item => item.RelativePath.Length)))
                    .Where(subtitle => subtitle.Owner is not null)
                    .ToLookup(subtitle => subtitle.Owner!.Id, subtitle => subtitle.File);
                foreach (var file in media)
                {
                    // A bundled game's relative paths can be referenced by a CUE or playlist.
                    // Leave such releases intact; a lone ROM has no internal references to invalidate.
                    if (file.MediaTypeId == MediaType.Game && (present.Any(item => Path.GetExtension(item.RelativePath).ToLowerInvariant() is ".cue" or ".m3u" or ".m3u8")
                        || present.Count(item => item.RelativePath.StartsWith(Folder(file.RelativePath), StringComparison.OrdinalIgnoreCase)) != 1)) continue;
                    var current = Stem(file.RelativePath);
                    if (Target(file, patterns) is not { } target || IsNamed(current, target))
                    {
                        continue;
                    }

                    var group = subtitles[file.Id].Select(subtitle => (File: subtitle, Suffix: subtitle.RelativePath[current.Length..])).Where(subtitle => !subtitle.Suffix.Contains('/'))
                        .Prepend((File: file, Suffix: Path.GetExtension(file.RelativePath))).ToArray();
                    var stem = Enumerable.Range(1, 99).Select(attempt => LibraryNaming.Alternative(target, attempt))
                        .FirstOrDefault(candidate => group.All(item => !taken.Contains(candidate + item.Suffix) && LibraryNaming.IsStorable(candidate + item.Suffix)));
                    if (stem is null)
                    {
                        continue;
                    }

                    foreach (var (item, suffix) in group)
                    {
                        taken.Add(stem + suffix);
                        renames.Add(new LibraryRenameData(item.Id, file.Id, item.LibraryRootId, item.LibraryRootName, item.RelativePath, stem + suffix));
                    }
                }
            }

            return renames;
        }

        // The part of the plan that names one title's files; the whole plan still decides which names are taken.
        public static IReadOnlyList<LibraryRenameData> PlanFor(IReadOnlyList<LibraryRenameFileData> files, NamingSet patterns, long? workId)
        {
            var plan = Plan(files, patterns);
            if (workId is null)
            {
                return plan;
            }

            var own = files.Where(file => file.WorkId == workId).Select(file => file.Id).ToHashSet();
            return plan.Where(rename => own.Contains(rename.MediaStoredFileId)).ToArray();
        }

        // Applying refuses a plan that differs from the preview.
        public static string Fingerprint(IReadOnlyList<LibraryRenameData> renames)
        {
            var text = new StringBuilder();
            foreach (var rename in renames)
            {
                text.Append(rename.StoredFileId).Append('\t').Append(rename.RelativePath).Append('\t').Append(rename.TargetPath).Append('\n');
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }

        // Identified files use the same configured patterns as imports; game bundles remain together.
        private static string? Target(LibraryRenameFileData file, NamingSet patterns)
        {
            if (file.CanonicalTitle is not { } title)
            {
                return null;
            }

            return (file.MediaTypeId, file.WorkUnitTypeId) switch
            {
                (MediaType.Movie, null) => NamingPattern.Apply(patterns.Pattern(MediaType.Movie, file.MediaSubtypeId), Values(file, title, null, null, patterns)),
                (MediaType.Series, WorkUnitType.Episode) when file.ParentUnitNumber is { } season => NamingPattern.Apply(patterns.Pattern(MediaType.Series, file.MediaSubtypeId, Jularr.Backend.Logic.Acquisition.ReleaseParser.TryParse(file.ReleaseName ?? Path.GetFileNameWithoutExtension(file.RelativePath), out var release) && release.AirDate is not null), Values(file, title, season, file.UnitNumber, patterns)),
                (MediaType.Book, null or WorkUnitType.Volume) when file.MediaSubtypeId == MediaSubtype.Manga => NamingPattern.Apply(patterns.Pattern(MediaType.Book, file.MediaSubtypeId), Values(file, title, null, null, patterns)),
                (MediaType.Book, null) => NamingPattern.Apply(patterns.Pattern(MediaType.Book, file.MediaSubtypeId), Values(file, title, null, null, patterns)),
                (MediaType.Music, WorkUnitType.Track) when file.UnitTitle is not null => NamingPattern.Apply(patterns.Pattern(MediaType.Music, file.MediaSubtypeId), Values(file, title, null, null, patterns)),
                (MediaType.Game, null) => NamingPattern.Apply(patterns.Pattern(MediaType.Game, file.MediaSubtypeId), Values(file, title, null, null, patterns)),
                _ => null
            };
        }

        private static NamingValues Values(LibraryRenameFileData file, string title, decimal? season, decimal? episode, NamingSet patterns) =>
            new NamingValues(title, file.ReleaseYear, season, episode, file.UnitTitle, QualityOf(file), file.ReleaseGroup, file.Edition, file.Revision)
            {
                MediaTypeId = file.MediaTypeId, MediaSubtypeId = file.MediaSubtypeId, EpisodeNumbers = JsonSerializer.Deserialize<decimal[]>(file.EpisodeNumbers) ?? [],
                Absolute = file.AbsoluteNumber, AbsoluteNumbers = JsonSerializer.Deserialize<decimal[]>(file.AbsoluteNumbers) ?? [],
                Author = Jularr.Backend.Logic.Acquisition.BookReleaseJudge.Authors(file.Author).FirstOrDefault(), Artist = file.Author,
                Track = file.WorkUnitTypeId == WorkUnitType.Track ? file.UnitNumber : null,
                TrackTitle = file.UnitTitle, Volume = file.WorkUnitTypeId == WorkUnitType.Volume ? file.UnitNumber : null,
                Language = file.Language, Disc = file.DiscNumber, DiscCount = file.DiscCount, Platform = file.Platform ?? (file.MediaTypeId == MediaType.Game ? "Unknown" : null),
                Region = file.MediaTypeId == MediaType.Game ? GameFileName.Facts(file.ReleaseName ?? file.RelativePath).Region : null,
                GameVersion = file.MediaTypeId == MediaType.Game ? GameFileName.Facts(file.ReleaseName ?? file.RelativePath).Version : null,
                Format = Path.GetExtension(file.RelativePath).TrimStart('.').ToUpperInvariant(),
                Rules = patterns.Rules(file.MediaTypeId),
                AirDate = file.UnitReleasedOn,
                ProviderIds = JsonSerializer.Deserialize<Dictionary<string, string>>(file.ProviderIds),
                OriginalFilename = Path.GetFileNameWithoutExtension(file.RelativePath),
                OriginalReleaseTitle = file.ReleaseName,
                Collection = file.Collection,
                CustomFormats = JsonSerializer.Deserialize<Dictionary<string, string>>(file.CustomFormats),
                MediaInfo = MediaDetails(file.MediaTracks),
                LocalizedTitles = JsonSerializer.Deserialize<Dictionary<string, string>>(file.LocalizedTitles)
            };

        internal static IReadOnlyDictionary<string, string> MediaDetails(string json)
        {
            var tracks = JsonSerializer.Deserialize<NamingTrack[]>(json) ?? [];
            var video = tracks.FirstOrDefault(track => track.MediaTrackTypeId == 1);
            var audio = tracks.FirstOrDefault(track => track.MediaTrackTypeId == 2);
            var languages = tracks.Where(track => track.MediaTrackTypeId == 2)
                .Select(track => Jularr.Backend.Logic.Media.LanguageCodes.ToTwoLetter(track.Language)?.ToUpperInvariant())
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            var subtitles = tracks.Where(track => track.MediaTrackTypeId == 3)
                .Select(track => Jularr.Backend.Logic.Media.LanguageCodes.ToTwoLetter(track.Language)?.ToUpperInvariant())
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            var videoCodec = video?.Codec ?? "";
            var audioCodec = audio?.Codec ?? "";
            var allAudio = languages.Length > 0 ? "[" + string.Join('+', languages) + "]" : "";
            var subtitleText = subtitles.Length > 0 ? "[" + string.Join('+', subtitles) + "]" : "";
            var channels = audio?.Channels switch
            {
                1 => "1.0",
                2 => "2.0",
                6 => "5.1",
                8 => "7.1",
                { } count => count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => ""
            };

            return new Dictionary<string, string>
            {
                ["MediaInfo VideoCodec"] = videoCodec,
                ["MediaInfo AudioCodec"] = audioCodec,
                ["MediaInfo AudioChannels"] = channels,
                ["MediaInfo AudioLanguages"] = languages is ["EN"] ? "" : allAudio,
                ["MediaInfo AudioLanguagesAll"] = allAudio,
                ["MediaInfo SubtitleLanguages"] = subtitleText,
                ["MediaInfo SubtitleLanguagesAll"] = subtitleText,
                ["MediaInfo VideoBitDepth"] = video?.BitDepth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ["MediaInfo VideoDynamicRange"] = video?.DynamicRange is { Length: > 0 } and not "DV SDR" ? "HDR" : "",
                ["MediaInfo VideoDynamicRangeType"] = video?.DynamicRange ?? "",
                ["MediaInfo 3D"] = video?.Is3D == true ? "3D" : "",
                ["MediaInfo Simple"] = string.Join(' ', new[] { videoCodec, audioCodec }.Where(value => value.Length > 0)),
                ["MediaInfo Full"] = string.Join(' ', new[] { videoCodec, audioCodec, allAudio, subtitleText }.Where(value => value.Length > 0))
            };
        }

        private sealed record NamingTrack(int MediaTrackTypeId, string? Codec, string? Language, int? Channels, int? BitDepth = null, string? DynamicRange = null, bool Is3D = false);

        // The stored release quality, else a tag only an older name carries, else the analysed width, wide formats included.
        private static string? QualityOf(LibraryRenameFileData file) => file.QualityKey
            ?? (QualityTag().Match(Path.GetFileNameWithoutExtension(file.RelativePath)) is { Success: true } match
                ? match.Groups[1].Value
                : file.Width switch
                {
                    >= 3500 => "2160p",
                    >= 1800 => "1080p",
                    >= 1200 => "720p",
                    _ => null
                });

        // A counted alternative is already in place.
        private static bool IsNamed(string current, string target) => current.Equals(target, StringComparison.OrdinalIgnoreCase)
            || current.StartsWith(target, StringComparison.OrdinalIgnoreCase) && Counter().IsMatch(current[target.Length..]);

        private static string Folder(string relativePath) => relativePath.LastIndexOf('/') is var slash and >= 0 ? relativePath[..(slash + 1)] : "";

        private static string Stem(string relativePath) => relativePath[..^Path.GetExtension(relativePath).Length];

        [GeneratedRegex(@" \[([^\[\]]+)\](?: \(\d+\))?$")]
        private static partial Regex QualityTag();

        [GeneratedRegex(@"^ \(\d+\)$")]
        private static partial Regex Counter();
    }
}
