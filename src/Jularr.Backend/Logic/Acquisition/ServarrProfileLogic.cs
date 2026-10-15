using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Integrations.Data.Servarr;

namespace Jularr.Backend.Logic.Acquisition
{
    // Sonarr and Radarr profiles in Jularr's model: qualities by source and resolution, custom formats as release rules where one condition field expresses them.
    internal static class ServarrProfileLogic
    {
        // At this score and below a custom format is a hard reject, as such profiles use it.
        private const int RejectScore = -10000;
        private const int MaximumScore = 1000;
        private const int MaximumRules = 50;
        private const int MaximumName = 64;
        private const int MaximumValue = 1000;

        public static TakenOverProfile Map(ServarrQualityProfile profile, IReadOnlyDictionary<int, ServarrCustomFormat> formats, MediaType mediaType)
        {
            // Best first, as Jularr orders qualities; a group's cutoff is its best quality.
            var allowed = Ordered(profile.Qualities.Where(quality => quality.IsAllowed));
            var cutoff = profile.UpgradeAllowed ? Ordered(profile.Qualities.Where(quality => quality.IsCutoff)).FirstOrDefault(allowed.Contains) : null;
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

            return new TakenOverProfile(input, rules, skipped);
        }

        private static string[] Ordered(IEnumerable<ServarrQuality> qualities)
        {
            var keys = qualities.Select(Quality).OfType<string>().ToHashSet(StringComparer.Ordinal);
            return [.. VideoQualities.Order.Where(keys.Contains)];
        }

        // Television, web and Blu-ray (also remux) in Jularr's five sizes; DVD, cam and raw sources have no Jularr quality.
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
