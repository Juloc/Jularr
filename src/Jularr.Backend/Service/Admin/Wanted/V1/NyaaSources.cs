using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.Nyaa;
using Jularr.Integrations.Logic.Nyaa;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    // The torrent index offers literature as torrent files; the torrent client fetches them.
    internal sealed class NyaaSources(NyaaClient client)
    {
        // The categories of the literature: English translated, the other languages and the raw text.
        private const string English = "3_1";
        private const string Translated = "3_2";
        private const string Raw = "3_3";

        // The search of one title; the categories of the profile's languages are asked.
        public async Task<IReadOnlyList<ReleaseOffer>?> SearchAsync(AcquisitionConnectionData source, WantedTargetData target, CancellationToken ct)
        {
            if (target.Definition is null)
            {
                return [];
            }

            var categories = Categories(AcquisitionSelectionLogic.ReadProfile(target));
            if (categories.Count == 0)
            {
                return [];
            }

            try
            {
                var found = await client.SearchAsync(Site(source), target.CanonicalTitle, categories.Keys, ct);
                return [.. found.Where(entry => categories.ContainsKey(entry.Category)).Select(entry => Offer(source, entry, categories[entry.Category]))];
            }
            catch (Exception exception) when (exception is NyaaException or HttpRequestException
                || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                return null;
            }
        }

        // The categories of the profile: the languages it asks for and the fallback it names.
        private static Dictionary<string, string?> Categories(QualityProfile profile)
        {
            var categories = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var language in profile.LanguageOrder)
            {
                var code = LanguageCodes.ToTwoLetter(language);
                if (code is null)
                {
                    continue;
                }

                // The English files stand in their own category; the others share the translated one.
                categories[code == "en" ? English : Translated] = code == "en" ? "en" : null;
            }

            switch (profile.LanguageFallback)
            {
                case LanguageFallbackType.Original:
                    categories[Raw] = null;
                    break;
                case LanguageFallbackType.English:
                    categories[English] = "en";
                    break;
                case LanguageFallbackType.None:
                    break;
            }

            return categories;
        }

        // A search that answers proves the index; null means healthy.
        public async Task<string?> TestAsync(AcquisitionConnectionData source, CancellationToken ct)
        {
            try
            {
                await client.SearchAsync(Site(source), "Jularr", [English], ct);
                return null;
            }
            catch (Exception exception) when (exception is NyaaException or HttpRequestException
                || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                return "acquisition_unavailable";
            }
        }

        private static ReleaseOffer Offer(AcquisitionConnectionData source, NyaaEntry entry, string? language)
        {
            var code = language ?? LanguageOf(entry.Title);
            var name = entry.Title + (code is { Length: > 0 } ? " (" + code.ToUpperInvariant() + ")" : "");
            return new ReleaseOffer(name.Length <= 1000 ? name : name[..1000], source.Id, source.Name, source.Priority, "nyaa:" + entry.Id,
                entry.DownloadUri is { } download ? new Uri(download) : NyaaClient.TorrentUri(entry.Id, entry.ViewUri), entry.ViewUri, entry.SizeBytes, entry.Published, ["title"]);
        }

        // The title names the language of the file: (FR) or [Manga FR].
        private static string? LanguageOf(string title)
        {
            foreach (Match group in Regex.Matches(title, @"[\[(]([^[\]()]{1,16})[\])]"))
            {
                var words = group.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var index = words.Length - 1; index >= 0; index--)
                {
                    var code = LanguageCodes.ToTwoLetter(words[index]);
                    if (code is not null)
                    {
                        return code;
                    }
                }
            }

            return null;
        }

        private static Uri Site(AcquisitionConnectionData source) => new(source.HttpAddress.TrimEnd('/') + "/");
    }
}
