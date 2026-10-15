using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.Servarr;

namespace Jularr.Backend.Logic.Acquisition
{
    // Servarr profiles in Jularr's model: qualities by source and resolution or by name, custom formats as release rules where one condition field expresses them.
    internal static class ServarrProfileLogic
    {
        // At this score and below a custom format is a hard reject, as such profiles use it.
        private const int RejectScore = -10000;
        private const int MaximumScore = 1000;
        private const int MaximumRules = 50;
        private const int MaximumName = 64;
        private const int MaximumValue = 1000;

        public static TakenOverProfile Map(ServarrQualityProfile profile, IReadOnlyDictionary<int, ServarrCustomFormat> formats, MediaType mediaType, ServarrMetadataProfile? metadata = null)
        {
            // Best first, as Jularr orders qualities; a group's cutoff is its best quality.
            var allowed = Ordered(profile.Qualities.Where(quality => quality.IsAllowed), mediaType);
            var cutoff = profile.UpgradeAllowed ? Ordered(profile.Qualities.Where(quality => quality.IsCutoff), mediaType).FirstOrDefault(allowed.Contains) : null;
            var input = new AcquisitionProfileInputData(null, Cut(profile.Name, 128), mediaType, null, false, allowed, profile.UpgradeAllowed && cutoff is not null, cutoff, null, null, [], [], null, 0,
                [], null);

            // The strongest formats first, up to the rule limit.
            var rules = new List<ReleaseScoreRule>();
            var skipped = new List<string>();
            foreach (var (formatId, score) in profile.FormatScores.Where(pair => pair.Value != 0).OrderByDescending(pair => Math.Abs(pair.Value)))
            {
                if (!formats.TryGetValue(formatId, out var format)) continue;
                if (rules.Count < MaximumRules && Rule(format, score) is { } rule) rules.Add(rule);
                else skipped.Add(format.Name);
            }

            return new TakenOverProfile(input, rules, skipped, metadata is null ? null : Monitoring(metadata));
        }

        // Lidarr's release types and statuses; Readarr's languages as two letters and its least pages.
        public static ReleaseMonitoring Monitoring(ServarrMetadataProfile metadata) => new([.. metadata.PrimaryTypes], [.. metadata.SecondaryTypes], [.. metadata.ReleaseStatuses],
            [.. metadata.Languages.Select(LanguageCodes.ToTwoLetter).OfType<string>().Distinct(StringComparer.Ordinal)], metadata.MinimumPages);

        // A profile of that name and type is used as it is; one without a known quality is left out; otherwise it is created with its rules.
        public static async Task<TakenOverProfileOutcome> TakeOverProfileAsync(this LogicContext ctx, TakenOverProfile mapped, IReadOnlyList<AcquisitionProfileNameData> existing, bool apply,
            CancellationToken ct)
        {
            if (existing.FirstOrDefault(profile => profile.MediaTypeId == mapped.Input.MediaTypeId && string.Equals(profile.Name, mapped.Input.Name, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                return new TakenOverProfileOutcome("takeover_exists", same.Id);
            }

            if (mapped.Input.AllowedQualities.Length == 0) return new TakenOverProfileOutcome("takeover_no_qualities", null);
            if (!apply) return new TakenOverProfileOutcome("takeover_will_add", null);
            var id = await ctx.CreateAcquisitionProfileAsync(mapped.Input, ct);
            if (mapped.Rules.Count > 0 || mapped.Monitoring is not null) await ctx.AppendProfileRulesAsync(id, mapped.Rules, mapped.Monitoring, ct);
            return new TakenOverProfileOutcome("takeover_added", id);
        }

        private static string[] Ordered(IEnumerable<ServarrQuality> qualities, MediaType mediaType)
        {
            var keys = qualities.Select(quality => mediaType switch
            {
                MediaType.Music => MusicQuality(quality.Name),
                MediaType.Book => BookQuality(quality.Name),
                _ => Quality(quality)
            }).OfType<string>().ToHashSet(StringComparer.Ordinal);
            return [.. ProfileQualities.For(mediaType, null).Where(keys.Contains)];
        }

        // Lidarr's names: lossless as they are, MP3 and AAC by their rate; other formats are audio of any kind.
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

        // Readarr's e-book formats; its audiobook formats have no book quality.
        private static string? BookQuality(string? name) => name?.Trim().ToUpperInvariant() switch
        {
            "EPUB" => "EPUB",
            "AZW3" => "AZW3",
            "MOBI" => "MOBI",
            "PDF" => "PDF",
            "UNKNOWN TEXT" => "EBOOK",
            _ => null
        };

        // Television, web and Blu-ray in Jularr's five sizes; DVD, cam and raw sources have no Jularr quality.
        private static string? Quality(ServarrQuality quality)
        {
            var source = quality.Source?.ToLowerInvariant() switch
            {
                "television" or "tv" => "HDTV",
                "web" or "webdl" or "webrip" => "WEB",
                "bluray" => "BLURAY",
                _ => null
            };
            var key = source + "-" + quality.Resolution + "p";
            return source is not null && VideoQualities.Order.Contains(key, StringComparer.Ordinal) ? key : null;
        }

        // One field carries all conditions; required ones must match, optional ones are alternatives, negated ones must not match.
        private static ReleaseScoreRule? Rule(ServarrCustomFormat format, int score)
        {
            var fields = format.Conditions.Select(condition => Field(condition.Implementation)).Distinct().ToArray();
            if (format.Conditions.Count == 0 || fields.Length != 1 || fields[0] is not { } field || format.Conditions.Any(condition => string.IsNullOrWhiteSpace(condition.Value))) return null;
            if (format.Conditions.Any(condition => condition.Negate && !condition.Required)) return null;

            string value;
            if (format.Conditions is [{ Negate: false } single])
            {
                value = single.Value!;
            }
            else
            {
                var required = format.Conditions.Where(condition => condition.Required && !condition.Negate).Select(condition => "(?=.*(?:" + condition.Value + "))");
                var excluded = format.Conditions.Where(condition => condition.Negate).Select(condition => "(?!.*(?:" + condition.Value + "))");
                var optional = format.Conditions.Where(condition => !condition.Required && !condition.Negate).Select(condition => condition.Value).ToArray();
                value = "^" + string.Concat(required) + string.Concat(excluded) + (optional.Length > 0 ? "(?=.*(?:" + string.Join("|", optional) + "))" : "");
            }

            if (value.Length > MaximumValue || !IsValid(value)) return null;
            var effect = score <= RejectScore ? ReleaseRuleEffect.Reject : score > 0 ? ReleaseRuleEffect.Prefer : ReleaseRuleEffect.Avoid;
            var points = effect == ReleaseRuleEffect.Reject ? 0 : Math.Min(Math.Abs(score), MaximumScore);
            return new ReleaseScoreRule(Cut(format.Name, MaximumName), field, ReleaseRuleMatch.Regex, value, effect == ReleaseRuleEffect.Avoid ? -points : points) { Effect = effect };
        }

        private static ReleaseRuleField? Field(string implementation) => implementation switch
        {
            "ReleaseTitleSpecification" or "EditionSpecification" => ReleaseRuleField.RawTitle,
            "ReleaseGroupSpecification" => ReleaseRuleField.ReleaseGroup,
            _ => null
        };

        private static bool IsValid(string pattern)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string Cut(string value, int length) => value.Length <= length ? value : value[..length];
    }
}
