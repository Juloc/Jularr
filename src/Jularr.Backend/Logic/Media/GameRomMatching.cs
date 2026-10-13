using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Media
{
    // Cartridge platforms whose verified dumps the libretro No-Intro DATs name by hash.
    internal static partial class GameRomMatching
    {
        // ROM files above this size are disc images or not verifiable dumps.
        public const long MaximumBytes = 512L * 1024 * 1024;

        public static readonly IReadOnlyList<RetroSystem> Systems =
        [
            new("Nintendo - Nintendo Entertainment System", 18, "Nintendo Entertainment System", "NES", ".nes") { Emulator = "fceumm" },
            new("Nintendo - Super Nintendo Entertainment System", 19, "Super Nintendo Entertainment System", "SNES", ".sfc", ".smc") { CopierHeader = 512, Emulator = "snes9x" },
            new("Nintendo - Nintendo 64", 4, "Nintendo 64", "N64", ".z64", ".n64", ".v64") { Emulator = "mupen64plus_next" },
            new("Nintendo - Game Boy", 33, "Game Boy", "GB", ".gb") { Emulator = "gambatte" },
            new("Nintendo - Game Boy Color", 22, "Game Boy Color", "GBC", ".gbc") { Emulator = "gambatte" },
            new("Nintendo - Game Boy Advance", 24, "Game Boy Advance", "GBA", ".gba") { Emulator = "mgba" },
            new("Nintendo - Nintendo DS", 20, "Nintendo DS", "NDS", ".nds") { Emulator = "melonds" },
            new("Nintendo - Virtual Boy", 87, "Virtual Boy", "VB", ".vb") { Emulator = "beetle_vb" },
            new("Sega - Mega Drive - Genesis", 29, "Sega Mega Drive/Genesis", "Genesis", ".md", ".gen") { Emulator = "segaMD" },
            new("Sega - Master System - Mark III", 64, "Sega Master System/Mark III", "SMS", ".sms") { Emulator = "genesis_plus_gx" },
            new("Sega - Game Gear", 35, "Sega Game Gear", "Game Gear", ".gg") { Emulator = "segaGG" },
            new("Atari - 2600", 59, "Atari 2600", "Atari 2600", ".a26") { Emulator = "stella2014" },
            new("Atari - Lynx", 61, "Atari Lynx", "Lynx", ".lnx", ".lyx") { Emulator = "handy" },
            new("NEC - PC Engine - TurboGrafx 16", 86, "TurboGrafx-16/PC Engine", "TG16", ".pce") { Emulator = "mednafen_pce" },
            new("SNK - Neo Geo Pocket Color", 120, "Neo Geo Pocket Color", "NGPC", ".ngc") { Emulator = "mednafen_ngp" },
            new("Bandai - WonderSwan", 57, "WonderSwan", "WS", ".ws") { Emulator = "mednafen_wswan" },
            new("Bandai - WonderSwan Color", 123, "WonderSwan Color", "WSC", ".wsc") { Emulator = "mednafen_wswan" }
        ];

        private static readonly FrozenDictionary<string, RetroSystem> ByExtension = Systems
            .SelectMany(system => system.Extensions.Select(extension => (extension, system))).ToFrozenDictionary(item => item.extension, item => item.system, StringComparer.OrdinalIgnoreCase);

        // Markdown shares .md; Mega Drive dumps count inside archives only.
        public static readonly IReadOnlySet<string> FileExtensions = ByExtension.Keys.Where(extension => extension != ".md").ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        // Files a scan hashes: ROMs and archives holding a single one.
        public static readonly string FilePattern = @"\.(" + string.Join('|', FileExtensions.Select(extension => extension[1..]).Order(StringComparer.Ordinal)) + "|zip)$";

        [GeneratedRegex(@"\s*[\(\[].*$")]
        private static partial Regex Tags();

        [GeneratedRegex(@"^(.+?), (The|A|An)( - .*)?$")]
        private static partial Regex Article();

        [GeneratedRegex(@"[^\p{L}\p{N}]+")]
        private static partial Regex Separators();

        public static RetroSystem? System(string fileName) => ByExtension.GetValueOrDefault(Path.GetExtension(fileName));

        // Platforms the browser plays; a game's platform decides, never its file name.
        public static readonly int[] EmulatedPlatforms = [.. Systems.Select(system => system.IgdbId)];

        public static RetroSystem? Emulated(int? igdbId) => Systems.FirstOrDefault(system => system.IgdbId == igdbId);

        // Copier headers precede some dumps; the DATs hash the bare ROM.
        public static int HeaderBytes(RetroSystem system, long length) => system.CopierHeader > 0 && length % 1024 == system.CopierHeader ? system.CopierHeader : 0;

        // "Legend of Zelda, The - A Link to the Past (USA)" names "The Legend of Zelda - A Link to the Past".
        public static string Title(string datName)
        {
            var title = Tags().Replace(datName, "").Trim();
            return Article().Match(title) is { Success: true } match ? match.Groups[2].Value + " " + match.Groups[1].Value + match.Groups[3].Value : title;
        }

        // Titles compare without case, punctuation or spacing.
        public static string Key(string title) => Separators().Replace(title.ToLowerInvariant(), "");
    }

    internal sealed class RetroSystem
    {
        public RetroSystem(string dat, int igdbId, string name, string abbreviation, params string[] extensions)
        {
            Dat = dat;
            IgdbId = igdbId;
            Name = name;
            Abbreviation = abbreviation;
            Extensions = extensions;
        }

        // The DAT's file name in the libretro database.
        public string Dat { get; }
        public int IgdbId { get; }
        public string Name { get; }
        public string Abbreviation { get; }
        public IReadOnlyList<string> Extensions { get; }
        public int CopierHeader { get; init; }

        // What EmulatorJS starts: its system name where that selects a bundled core, otherwise the core itself.
        public required string Emulator { get; init; }
    }
}
