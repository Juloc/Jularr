using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // Scores a parsed release against a profile, for every media type.
    internal static class ReleaseScorer
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

        public static ReleaseScoreResult Score(QualityProfile profile, ReleaseCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(candidate.Release);

            var qualityKey = ReleaseQuality.GetKey(candidate.Release, profile.IsMovie);
            var item = profile.Item(qualityKey);
            var qualityRank = IndexOf(profile.QualityOrder, item);
            var rejections = new List<ReleaseReason>();
            var scoreReasons = new List<ReleaseReason>();

            if (profile.AllowedQualities.Length > 0 &&
                !profile.AllowedQualities.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                rejections.Add(new ReleaseReason("QualityNotAllowed", qualityKey));
            }

            if ((profile.MinimumSizeBytes is not null || profile.MaximumSizeBytes is not null) &&
                candidate.SizeBytes is null)
            {
                rejections.Add(new ReleaseReason("SizeUnknown"));
            }
            else
            {
                if (profile.MinimumSizeBytes is long minimumSize &&
                    candidate.SizeBytes is long actualSize &&
                    actualSize < minimumSize)
                {
                    rejections.Add(new ReleaseReason("SizeBelowMinimum", actualSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }

                if (profile.MaximumSizeBytes is long maximumSize &&
                    candidate.SizeBytes is long actualMaximumSize &&
                    actualMaximumSize > maximumSize)
                {
                    rejections.Add(new ReleaseReason("SizeAboveMaximum", actualMaximumSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }

            // Quality sizes use known total duration, or the work runtime per episode. Packs need a known total duration.
            if ((candidate.RuntimeMinutes ?? profile.RuntimeMinutes) is { } minutes && candidate.SizeBytes is { } bytes && profile.Sizes.TryGetValue(qualityKey, out var size)
                && (!candidate.Release.IsSeasonPack || candidate.RuntimeMinutes is not null))
            {
                var episodes = candidate.Release.EpisodeStart is { } first && candidate.Release.EpisodeEnd is { } last && last >= first ? last - first + 1 : 1;
                var megabytes = bytes / 1_048_576m;
                var length = candidate.RuntimeMinutes is not null ? minutes : minutes * episodes;
                if (megabytes < size.Minimum * length)
                {
                    rejections.Add(new ReleaseReason("SizeBelowQualityMinimum", qualityKey));
                }
                else if (size.Maximum is { } maximum && megabytes > maximum * length)
                {
                    rejections.Add(new ReleaseReason("SizeAboveQualityMaximum", qualityKey));
                }
            }

            // Release profiles as in Sonarr: one required term must appear, no ignored term may.
            foreach (var terms in profile.ReleaseProfiles.Where(terms => terms.IndexerId is null || terms.IndexerId == candidate.IndexerId))
            {
                if (terms.Required.Length > 0 && !terms.Required.Any(term => Contains(candidate.Release.RawTitle, term)))
                {
                    rejections.Add(new ReleaseReason("MissingRequiredTerm", string.Join(", ", terms.Required)));
                }

                foreach (var ignored in terms.Ignored.Where(term => Contains(candidate.Release.RawTitle, term)))
                {
                    rejections.Add(new ReleaseReason("ContainsRejectedTerm", ignored));
                }
            }

            var infoReasons = new List<string>();

            // Custom formats add their profile scores; the sum must reach the profile's minimum.
            var context = new FormatContext(profile.IsMovie, profile.OriginalLanguage, profile.Year);
            var formatScore = 0;
            var formatPoints = new List<ScorePoint>();
            foreach (var (format, points) in profile.Formats)
            {
                if (!CustomFormatMatcher.TryMatch(format, candidate, context, out var matches))
                {
                    rejections.Add(new ReleaseReason("FormatEvaluationFailed", format.Name));
                }
                else if (matches)
                {
                    formatScore += points;
                    formatPoints.Add(new ScorePoint("CustomFormat", format.Name, points));
                    scoreReasons.Add(new ReleaseReason("FormatScore", $"{format.Name}: {(points >= 0 ? "+" : "")}{points}"));
                }
            }

            if (formatScore < profile.MinimumFormatScore)
            {
                rejections.Add(new ReleaseReason("FormatScoreBelowMinimum", formatScore.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            var score = formatScore;
            return new ReleaseScoreResult(candidate, rejections.Count == 0, score, qualityKey, qualityRank, rejections, scoreReasons)
            {
                InfoReasons = infoReasons,
                FormatScore = formatScore,
                FormatPoints = formatPoints
            };
        }

        public static IReadOnlyList<string> ValidateProfile(QualityProfile profile)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                errors.Add("Profile ID is required.");
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                errors.Add("Profile name is required.");
            }

            if (profile.AllowedQualities is null ||
                profile.QualityOrder is null)
            {
                errors.Add("Profile collections must not be null.");
                return errors;
            }

            if (profile.MinimumSizeBytes is < 0)
            {
                errors.Add("Minimum size must be zero or greater.");
            }

            if (profile.MaximumSizeBytes is < 0)
            {
                errors.Add("Maximum size must be zero or greater.");
            }

            if (profile.MinimumSizeBytes is long minimum &&
                profile.MaximumSizeBytes is long maximum &&
                minimum > maximum)
            {
                errors.Add("Minimum size must not exceed maximum size.");
            }

            if (profile.QualityOrder
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1))
            {
                errors.Add("Quality order contains duplicates.");
            }

            foreach (var allowed in profile.AllowedQualities)
            {
                if (!profile.QualityOrder.Contains(allowed, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"Allowed quality '{allowed}' is missing from quality order.");
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.UpgradeCutoffQuality) &&
                !profile.QualityOrder.Contains(profile.UpgradeCutoffQuality, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Upgrade cutoff '{profile.UpgradeCutoffQuality}' is missing from quality order.");
            }

            if (profile.UpgradeMinimumQualitySteps < 1)
            {
                errors.Add("The upgrade quality steps must be at least 1.");
            }

            return errors;
        }

        // A term in slashes is a pattern, as Sonarr writes /x26[45]/i; otherwise it is a phrase in any case.
        private static bool Contains(string title, string term)
        {
            if (term.Length > 2 && term[0] == '/' && term.LastIndexOf('/') is > 0 and var end)
            {
                try
                {
                    return Regex.IsMatch(title, term[1..end], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                }
                catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
                {
                    return false;
                }
            }

            return title.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        private static int IndexOf(IReadOnlyList<string> values, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return int.MaxValue;
            }

            for (var index = 0; index < values.Count; index++)
            {
                if (values[index].Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return int.MaxValue;
        }

    }
}
