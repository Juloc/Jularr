using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.Servarr;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class ServarrProfileLogic
    {
        public static TakenOverProfile Map(ServarrQualityProfile profile, IReadOnlyDictionary<int, ServarrCustomFormat> formats, MediaType mediaType,
            ServarrMetadataProfile? metadata = null, ServarrFormatCatalog? catalog = null, string? displayName = null)
        {
            var known = ProfileQualities.For(mediaType, null);
            var groups = profile.Qualities.Where(quality => quality.GroupId is not null).GroupBy(quality => quality.GroupId!.Value)
                .Select(group =>
                {
                    var name = group.First().GroupName ?? "[" + group.Key.ToString(CultureInfo.InvariantCulture) + "]";
                    return (Id: group.Key, Group: new QualityGroup(group.Key, name, group.Select(quality => Quality(quality, mediaType)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
                }).Where(group => group.Group.Qualities.Length > 0).ToArray();
            string? Item(ServarrQuality quality) => quality.GroupId is { } id ? groups.FirstOrDefault(group => group.Id == id).Group?.Key : Quality(quality, mediaType);
            var order = profile.Qualities.Reverse().Select(Item).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            order.AddRange(known.Where(quality => !groups.Any(group => group.Group.Qualities.Contains(quality, StringComparer.OrdinalIgnoreCase)) && !order.Contains(quality, StringComparer.OrdinalIgnoreCase)));
            var allowed = profile.Qualities.Where(quality => quality.IsAllowed).Select(Item).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var cutoff = profile.UpgradeAllowed ? profile.Qualities.Where(quality => quality.IsCutoff).Select(Item).OfType<string>().FirstOrDefault() : null;

            var mapped = new List<CustomFormatImportData>();
            var skipped = new List<string>();
            foreach (var score in profile.FormatScores)
            {
                if (!formats.TryGetValue(score.Key, out var format))
                {
                    skipped.Add(score.Key.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                try
                {
                    mapped.Add(ServarrFormatMapping.Map(format, catalog ?? ServarrFormatCatalog.Portable(mediaType == MediaType.Movie), mediaType));
                }
                catch (DataValidationException)
                {
                    skipped.Add(format.Name);
                }
            }

            skipped.AddRange(profile.Qualities.Where(quality => quality.IsAllowed && Quality(quality, mediaType) is null).Select(quality => quality.Name ?? "?"));
            var input = new AcquisitionProfileInputData(null, TextLimit.Cut(displayName ?? profile.Name, 128)!, mediaType, null, false, allowed, profile.UpgradeAllowed, cutoff, null, null, null)
            {
                QualityOrder = [.. order],
                QualityGroups = groups.Select(group => group.Group).ToArray(),
                FormatScores = profile.FormatScores.Select(score => new FormatScore(score.Key, score.Value)).ToArray(),
                MinimumFormatScore = profile.MinimumFormatScore,
                CutoffFormatScore = profile.CutoffFormatScore,
                MinimumUpgradeFormatScore = profile.MinimumUpgradeFormatScore,
                Monitoring = metadata is null ? null : Monitoring(metadata)
            };
            return new TakenOverProfile(input, mapped, skipped);
        }

        public static ReleaseMonitoring Monitoring(ServarrMetadataProfile metadata) => new([.. metadata.PrimaryTypes], [.. metadata.SecondaryTypes], [.. metadata.ReleaseStatuses],
            [.. metadata.Languages.Select(LanguageCodes.ToTwoLetter).OfType<string>().Distinct(StringComparer.Ordinal)], metadata.MinimumPages);

        public static async Task<TakenOverProfileOutcome> TakeOverProfileAsync(this LogicContext ctx, TakenOverProfile mapped, ProfileImportReferenceData source,
            IReadOnlyList<AcquisitionProfileNameData> existing, bool apply, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."Version"
                FROM
                    "AcquisitionProfileImports" AS imported
                    JOIN "AcquisitionProfiles" AS profile ON profile."Id" = imported."AcquisitionProfileId"
                WHERE
                    imported."SourceUrl" = @SourceUrl
                    AND imported."SourceProfileId" = @SourceProfileId
                    AND imported."SourceMetadataProfileId" = @SourceMetadataProfileId
                    AND profile."MediaTypeId" = @MediaTypeId;
                """;
            sql.Parameters.Add(source);
            var linked = await ctx.ReadOptionalAsync<AcquisitionProfileKeyData>(sql, ct);
            if (linked is not null) return new TakenOverProfileOutcome("takeover_exists", linked.Id);
            if (mapped.SkippedFormats.Count > 0) return new TakenOverProfileOutcome("takeover_profile_unsupported", null);
            if (mapped.Input.AllowedQualities.Length == 0) return new TakenOverProfileOutcome("takeover_no_qualities", null);
            _ = AcquisitionProfileLogic.BuildDefinition(mapped.Input, null);
            if (!apply) return new TakenOverProfileOutcome("takeover_will_add", null);

            var imported = await ctx.ImportCustomFormatsAsync(source.MediaTypeId, mapped.Formats, ct);
            var keys = imported.ToDictionary(format => format.SourceId, format => format.Id);
            var original = mapped.Input;
            var name = original.Name;
            if (existing.Any(profile => profile.MediaTypeId == source.MediaTypeId && profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                var app = source.MediaTypeId switch { MediaType.Movie => "Radarr", MediaType.Series => "Sonarr", MediaType.Music => "Lidarr", _ => "Readarr" };
                name = TextLimit.Cut(name, 100) + " (" + app + " " + source.SourceProfileId.ToString(CultureInfo.InvariantCulture) + ")";
            }

            var input = new AcquisitionProfileInputData(null, name, original.MediaTypeId, original.MediaSubtypeId, false, original.AllowedQualities, original.UpgradeAllowed,
                original.UpgradeCutoffQuality, original.MinimumSizeMb, original.MaximumSizeMb, null)
            {
                QualityOrder = original.QualityOrder, QualityGroups = original.QualityGroups,
                FormatScores = original.FormatScores.Select(score => new FormatScore(keys[checked((int)score.FormatId)], score.Score)).ToArray(),
                MinimumFormatScore = original.MinimumFormatScore, CutoffFormatScore = original.CutoffFormatScore,
                MinimumUpgradeFormatScore = original.MinimumUpgradeFormatScore, Monitoring = original.Monitoring
            };
            var id = await ctx.CreateAcquisitionProfileAsync(input, ct);
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AcquisitionProfileImports" ("SourceUrl", "SourceProfileId", "AcquisitionProfileId", "SourceMetadataProfileId")
                VALUES
                    (@SourceUrl, @SourceProfileId, @AcquisitionProfileId, @SourceMetadataProfileId);
                """;
            sql.Parameters.Add(new ProfileImportLinkData(source.SourceUrl, source.SourceProfileId, id, source.SourceMetadataProfileId));
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new DataValidationException("Version", "invalid_transition");
            return new TakenOverProfileOutcome("takeover_added", id);
        }

        private static string? Quality(ServarrQuality quality, MediaType type)
        {
            if (type == MediaType.Music) return MusicQuality(quality.Name);
            if (type == MediaType.Book) return BookQuality(quality.Name);
            var known = ProfileQualities.For(type, null);
            if (known.FirstOrDefault(name => string.Equals(name, quality.Name, StringComparison.OrdinalIgnoreCase)) is { } named) return named;
            if (quality.Name is not null) return null;
            var source = quality.Source?.ToLowerInvariant();
            var candidate = source switch
            {
                "web" or "webdl" => "WEBDL-" + quality.Resolution + "p",
                "webrip" => "WEBRip-" + quality.Resolution + "p",
                "television" or "hdtv" => "HDTV-" + quality.Resolution + "p",
                "televisionraw" => "Raw-HD",
                "bluray" => "Bluray-" + quality.Resolution + "p",
                "blurayraw" => type == MediaType.Movie ? "Remux-" + quality.Resolution + "p" : "Bluray-" + quality.Resolution + "p Remux",
                "dvd" => "DVD",
                _ => null
            };
            return known.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        }

        private static string? MusicQuality(string? name) => name?.Trim() switch
        {
            null or "" or "Unknown" => null,
            { } value when value.Equals("FLAC 24bit", StringComparison.OrdinalIgnoreCase) => "FLAC-24",
            { } value when value.StartsWith("FLAC", StringComparison.OrdinalIgnoreCase) => "FLAC",
            { } value when value.StartsWith("ALAC", StringComparison.OrdinalIgnoreCase) => "ALAC",
            { } value when value.Equals("MP3-320", StringComparison.OrdinalIgnoreCase) => "MP3-320",
            { } value when value.Equals("MP3-VBR-V0", StringComparison.OrdinalIgnoreCase) => "MP3-V0",
            { } value when value.StartsWith("MP3", StringComparison.OrdinalIgnoreCase) => "MP3",
            { } value when value.StartsWith("AAC", StringComparison.OrdinalIgnoreCase) => "AAC",
            _ => "AUDIO"
        };

        private static string? BookQuality(string? name) => name?.Trim().ToUpperInvariant() switch
        {
            "EPUB" => "EPUB", "AZW3" => "AZW3", "MOBI" => "MOBI", "PDF" => "PDF", "UNKNOWN TEXT" => "EBOOK", _ => null
        };
    }
}
