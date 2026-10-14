using System.Security.Cryptography;
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
                SELECT
                    file."Id",
                    file."LibraryRootId",
                    root."Name" AS "LibraryRootName",
                    root."MediaTypeId",
                    root."MediaSubtypeId",
                    file."RelativePath",
                    file."MissingSince",
                    file."MediaAssetId",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    (
                        SELECT
                            fact."Value"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                            AND fact."FactKey" IN ('Author', 'Artist')
                        ORDER BY
                            fact."IsManual" DESC
                        LIMIT 1
                    ) AS "Author",
                    unit."WorkUnitTypeId",
                    unit."Number" AS "UnitNumber",
                    unit."Title" AS "UnitTitle",
                    parent."Number" AS "ParentUnitNumber",
                    file."QualityKey",
                    file."ReleaseGroup",
                    file."Edition",
                    file."Revision",
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
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
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

        public static IReadOnlyList<LibraryRenameData> Plan(IReadOnlyList<LibraryRenameFileData> files, IReadOnlyDictionary<MediaType, string> patterns)
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

        // Only identities with a fixed layout are renamed; games keep their release folders.
        private static string? Target(LibraryRenameFileData file, IReadOnlyDictionary<MediaType, string> patterns)
        {
            if (file.CanonicalTitle is not { } title)
            {
                return null;
            }

            return (file.MediaTypeId, file.WorkUnitTypeId) switch
            {
                (MediaType.Movie, null) => LibraryNaming.Video(patterns, MediaType.Movie, Values(file, title, null, null)),
                (MediaType.Series, WorkUnitType.Episode) when file.ParentUnitNumber is { } season => LibraryNaming.Video(patterns, MediaType.Series, Values(file, title, season, file.UnitNumber)),
                (MediaType.Book, null or WorkUnitType.Volume) when file.MediaSubtypeId == MediaSubtype.Manga => LibraryNaming.Volume(title, file.UnitNumber),
                (MediaType.Book, null) => LibraryNaming.Book(title, file.ReleaseYear, file.Author),
                (MediaType.Music, WorkUnitType.Track) when file.UnitTitle is { } name => LibraryNaming.Track(file.Author, title, file.ReleaseYear, file.UnitNumber!.Value, name),
                _ => null
            };
        }

        private static NamingValues Values(LibraryRenameFileData file, string title, decimal? season, decimal? episode) =>
            new(title, file.ReleaseYear, season, episode, file.UnitTitle, QualityOf(file), file.ReleaseGroup, file.Edition, file.Revision);

        // The quality tag the name already carries, else the stored release quality, else the analysed width, wide formats included.
        private static string? QualityOf(LibraryRenameFileData file) => QualityTag().Match(Path.GetFileNameWithoutExtension(file.RelativePath)) is { Success: true } match
            ? match.Groups[1].Value
            : file.QualityKey ?? file.Width switch
            {
                >= 3500 => "2160p",
                >= 1800 => "1080p",
                >= 1200 => "720p",
                _ => null
            };

        // A counted alternative is already in place.
        private static bool IsNamed(string current, string target) => current.Equals(target, StringComparison.OrdinalIgnoreCase)
            || current.StartsWith(target, StringComparison.OrdinalIgnoreCase) && Counter().IsMatch(current[target.Length..]);

        private static string Stem(string relativePath) => relativePath[..^Path.GetExtension(relativePath).Length];

        [GeneratedRegex(@" \[([^\[\]]+)\](?: \(\d+\))?$")]
        private static partial Regex QualityTag();

        [GeneratedRegex(@"^ \(\d+\)$")]
        private static partial Regex Counter();
    }
}
