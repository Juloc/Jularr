using System.Text.Json;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // The fonts a Matroska file carries, taken out once into the playback cache, so the player's libass can draw the file's own lettering.
    internal sealed class PlaybackFonts(MediaProbeClient probe, MediaTranscoder transcoder, StorageAccess storage, IOptions<MediaOptions> options)
    {
        public const int MaximumFonts = 40;
        public const long MaximumFontBytes = 16 * 1024 * 1024;

        private static readonly string[] Extensions = [".ttf", ".otf", ".ttc", ".woff", ".woff2"];
        private static readonly SemaphoreSlim Extractions = new(1, 1);

        public bool IsAvailable => probe.IsAvailable && transcoder.IsAvailable;

        public static bool IsFont(MediaAttachment attachment) =>
            attachment.FileName is { } name && Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            || attachment.MimeType is { } type && (type.Contains("font", StringComparison.OrdinalIgnoreCase) || type.Contains("truetype", StringComparison.OrdinalIgnoreCase) || type.Contains("opentype", StringComparison.OrdinalIgnoreCase));

        public string FolderOf(string cacheKey) => Path.Combine(options.Value.TranscodeCachePath, "fonts", cacheKey);

        // The names of the file's fonts by their stream index; taken out on the first ask, then read from the cache.
        public async Task<IReadOnlyDictionary<int, string>> ListAsync(StorageFiles files, string path, string cacheKey, CancellationToken ct)
        {
            var folder = FolderOf(cacheKey);
            var names = Path.Combine(folder, "names.json");
            if (File.Exists(names))
            {
                return JsonSerializer.Deserialize<Dictionary<int, string>>(await File.ReadAllTextAsync(names, ct)) ?? [];
            }

            if (!IsAvailable)
            {
                return new Dictionary<int, string>();
            }

            await Extractions.WaitAsync(ct);
            try
            {
                if (File.Exists(names))
                {
                    return JsonSerializer.Deserialize<Dictionary<int, string>>(await File.ReadAllTextAsync(names, ct)) ?? [];
                }

                using var input = storage.Input(files, path);
                var fonts = (await probe.AttachmentsAsync(input.Location, ct)).Where(IsFont).Take(MaximumFonts).ToList();
                Directory.CreateDirectory(folder);
                var found = new Dictionary<int, string>();
                foreach (var font in fonts)
                {
                    var target = Path.Combine(folder, font.Index.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + ".font");
                    await transcoder.DumpAttachmentAsync(input.Location, font.Index, target, ct);
                    if (File.Exists(target) && new FileInfo(target).Length is > 0 and <= MaximumFontBytes)
                    {
                        found[font.Index] = Path.GetFileName(font.FileName ?? "font") ?? "font";
                    }
                    else
                    {
                        File.Delete(target);
                    }
                }

                await File.WriteAllTextAsync(names + ".tmp", JsonSerializer.Serialize(found), ct);
                File.Move(names + ".tmp", names, true);
                return found;
            }
            finally
            {
                Extractions.Release();
            }
        }
    }
}
