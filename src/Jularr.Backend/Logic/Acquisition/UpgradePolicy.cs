using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The one cutoff and upgrade policy of every media type (Movie, TV, Anime, Music, Books).
    internal static class UpgradePolicy
    {
        // How often a target that is installed but not final is searched again for a better release.
        public static readonly TimeSpan SearchInterval = TimeSpan.FromHours(12);

        public static int RankOf(QualityProfile profile, string? quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
            {
                return int.MaxValue;
            }

            var item = profile.Item(quality);
            var index = Array.FindIndex(profile.QualityOrder, entry => entry.Equals(item, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }

        // The best (lowest-ranked) known quality among the installed versions of one target, or null when none is comparable.
        public static string? Best(QualityProfile profile, IEnumerable<string?> qualities) =>
            qualities
                .Where(quality => RankOf(profile, quality) != int.MaxValue)
                .OrderBy(quality => RankOf(profile, quality))
                .FirstOrDefault();

        // Whether the installed quality reaches the profile's upgrade cutoff.
        public static bool IsCutoffMet(QualityProfile profile, string? installedQuality)
        {
            var cutoffRank = RankOf(profile, profile.UpgradeCutoffQuality);
            var rank = RankOf(profile, installedQuality);
            return cutoffRank != int.MaxValue && rank != int.MaxValue && rank <= cutoffRank;
        }

        public static UpgradeAssessment Assess(QualityProfile profile, string? installedQuality)
        {
            ArgumentNullException.ThrowIfNull(profile);

            var rank = RankOf(profile, installedQuality);
            if (rank == int.MaxValue)
            {
                return new UpgradeAssessment(UpgradeState.Final, new ReleaseReason("InstalledQualityUnknown"));
            }

            if (!profile.UpgradeAllowed)
            {
                return new UpgradeAssessment(UpgradeState.Final, new ReleaseReason("UpgradesDisabled"));
            }

            if (IsCutoffMet(profile, installedQuality))
            {
                return new UpgradeAssessment(UpgradeState.Final, new ReleaseReason("CutoffMet", installedQuality));
            }

            return rank == 0
                ? new UpgradeAssessment(UpgradeState.Final, new ReleaseReason("BestQuality", installedQuality))
                : new UpgradeAssessment(UpgradeState.Upgradable, new ReleaseReason("BelowCutoff", installedQuality));
        }

        // Whether a candidate is a meaningful upgrade of what is installed: a better quality below the cutoff, or as in Sonarr the same quality with
        // a format score higher by at least the increment while the installed one is below the cutoff score.
        public static bool IsUpgrade(QualityProfile profile, string? installedQuality, string? candidateQuality, bool upgradeUnknownInstalled = false,
            int? installedFormatScore = null, int? candidateFormatScore = null, IReadOnlyList<string>? installedLanguages = null, IReadOnlyList<string>? candidateLanguages = null)
        {
            ArgumentNullException.ThrowIfNull(profile);

            var installed = RankOf(profile, installedQuality);
            var candidate = RankOf(profile, candidateQuality);
            if (!profile.UpgradeAllowed || installed == int.MaxValue && !upgradeUnknownInstalled)
            {
                return false;
            }

            var formatUpgrade = installedFormatScore is { } before && candidateFormatScore is { } after
                && before < profile.CutoffFormatScore && after >= before + Math.Max(1, profile.MinimumUpgradeFormatScore);
            if (candidate < installed)
            {
                return formatUpgrade || !IsCutoffMet(profile, installedQuality)
                    && (installed == int.MaxValue || installed - candidate >= Math.Max(1, profile.UpgradeMinimumQualitySteps));
            }

            var languageUpgrade = profile.IsBook && installedLanguages is not null && candidateLanguages is not null
                && candidateFormatScore >= installedFormatScore && LanguageRank(profile, candidateLanguages) < LanguageRank(profile, installedLanguages);
            return candidate == installed && (formatUpgrade || languageUpgrade);
        }

        // Requests supply the ordered languages; a quality group and format score still decide before this tie-break.
        public static int LanguageRank(QualityProfile profile, IReadOnlyList<string> languages)
        {
            if (profile.LanguageOrder.Length == 0) return 0;
            var best = languages.Select(language => Array.FindIndex(profile.LanguageOrder, wanted => Same(wanted, language)))
                .Where(index => index >= 0).DefaultIfEmpty(-1).Min();
            return best >= 0 ? best : languages.Count == 0 ? profile.LanguageOrder.Length : profile.LanguageOrder.Length + 1;
        }

        // The file carries one of the languages a request asks for.
        public static bool IsRequested(QualityProfile profile, IReadOnlyList<string> languages) => languages.Any(language => profile.LanguageOrder.Any(wanted => Same(wanted, language)));

        // The file carries the original language of the work; it is the next choice after the asked ones.
        public static bool IsOriginal(QualityProfile profile, IReadOnlyList<string> languages) =>
            profile.OriginalLanguage is { } original && languages.Any(language => Same(original, language));

        // The fallback when no file carries an asked language: the original language, English, or nothing.
        public static bool IsFallback(QualityProfile profile, IReadOnlyList<string> languages) => profile.LanguageFallback switch
        {
            LanguageFallbackType.English => languages.Any(language => Same("EN", language)),
            LanguageFallbackType.None => false,
            LanguageFallbackType.Any => true,
            _ => IsOriginal(profile, languages)
        };

        private static bool Same(string wanted, string language) =>
            language.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) || wanted.StartsWith(language, StringComparison.OrdinalIgnoreCase);
    }
}
