using System.Net;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Libretro;

namespace Jularr.Integrations.Logic.Libretro
{
    // The No-Intro DATs of the libretro-database (CC BY-SA 4.0), read from its public repository.
    internal sealed partial class LibretroClient(HttpClient http)
    {
        private static readonly Uri Repository = new("https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/");

        public async Task<LibretroDat> ReadDatAsync(string system, CancellationToken ct)
        {
            string text;
            try
            {
                using var response = await http.GetAsync(new Uri(Repository, Uri.EscapeDataString(system) + ".dat"), ct);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new LibretroException("The libretro database answered " + (int)response.StatusCode + ".");
                }

                text = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new LibretroException("The libretro database is unreachable.", exception);
            }

            // An unreadable answer never replaces a known DAT.
            var dat = Parse(text);
            return dat.Roms.Count > 0 ? dat : throw new LibretroException("The libretro DAT lists no dumps.");
        }

        // clrmamepro: a header block, then one block per game with its ROM lines.
        internal static LibretroDat Parse(string text)
        {
            var version = Version().Match(text) is { Success: true } header ? header.Groups[1].Value : null;
            var roms = new List<LibretroRom>();
            foreach (Match game in Game().Matches(text))
            {
                var name = game.Groups[1].Value.Trim();
                foreach (Match rom in Sha1().Matches(game.Groups[2].Value))
                {
                    if (name.Length is > 0 and <= 500)
                    {
                        roms.Add(new LibretroRom(name, rom.Groups[1].Value.ToLowerInvariant()));
                    }
                }
            }

            return new LibretroDat(version is { Length: <= 32 } ? version : null, roms);
        }

        [GeneratedRegex(@"^clrmamepro \([^)]*?\bversion ""([^""]*)""", RegexOptions.Singleline)]
        private static partial Regex Version();

        [GeneratedRegex(@"^game \(\s*name ""([^""]*)""(.*?)^\)", RegexOptions.Multiline | RegexOptions.Singleline)]
        private static partial Regex Game();

        [GeneratedRegex(@"\brom \(.*?\bsha1 ([0-9A-Fa-f]{40})\b")]
        private static partial Regex Sha1();
    }
}
