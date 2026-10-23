using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.AcoustId;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.System.Operation.V1
{
    // Assigns a music library's unassigned files to the album their MusicBrainz tag names. With AcoustID switched on and fpcalc installed, an untagged folder whose files'
    // fingerprints all match one album goes to that album; anything else stays for manual review. I/O stays outside transactions.
    internal sealed class MusicTagIdentification(MetadataCatalog catalog, AudioFingerprinter fingerprinter, AcoustIdClient acoustId, IDataProtectionProvider protection)
    {
        // MusicBrainz answers once a second and AcoustID three times; further albums follow on the next scan.
        private const int ImportsPerScan = 50;
        private const int FingerprintsPerScan = 200;

        public async Task IdentifyAsync(LogicContext context, long libraryRootId, StorageFiles storage, string directory, CancellationToken ct)
        {
            var albums = new Dictionary<string, List<(long StoredFileId, string RelativePath, AudioTags? Tags)>>(StringComparer.Ordinal);
            var untagged = new List<(long StoredFileId, string RelativePath)>();
            for (var afterId = 0L; ;)
            {
                var files = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadUnassignedFilesAsync(new StoredFileCursorData(libraryRootId, afterId), token), ct);
                if (files.Count == 0)
                {
                    break;
                }

                foreach (var file in files)
                {
                    afterId = file.Id;
                    if (LibraryFileTypes.GetAssetType(file.RelativePath) != MediaAssetType.Audio)
                    {
                        continue;
                    }

                    if (await ReadTagsAsync(storage, StoragePath.Combine(directory, file.RelativePath), ct) is { ReleaseGroupId: { } album } tags)
                    {
                        Add(albums, album, (file.Id, file.RelativePath, tags));
                    }
                    else
                    {
                        untagged.Add((file.Id, file.RelativePath));
                    }
                }
            }

            if (untagged.Count > 0 && fingerprinter.IsAvailable && await AcoustIdKeyAsync(context, ct) is { } key)
            {
                await FingerprintAsync(untagged, storage, directory, key, albums, ct);
            }

            if (albums.Count == 0)
            {
                return;
            }

            var known = (await context.ExecuteBatchAsync((ctx, token) => ctx.ReadMusicAlbumsAsync(new MusicAlbumLookupData([.. albums.Keys]), token), ct))
                .ToDictionary(album => album.ExternalId, StringComparer.Ordinal);
            var imports = 0;
            foreach (var (album, tracks) in albums)
            {
                if (!known.ContainsKey(album))
                {
                    if (imports++ >= ImportsPerScan)
                    {
                        continue;
                    }

                    try
                    {
                        await catalog.ImportAsync(context, MediaType.Music, null, MusicMetadata.Namespace, album, ct);
                    }
                    catch (Exception exception) when (exception is KeyNotFoundException or ServiceUnavailableException)
                    {
                        continue;
                    }

                    if ((await context.ExecuteBatchAsync((ctx, token) => ctx.ReadMusicAlbumsAsync(new MusicAlbumLookupData([album]), token), ct)).SingleOrDefault() is not { } imported)
                    {
                        continue;
                    }

                    known[album] = imported;
                }

                // In tag order, else in natural order, the files are the album's tracks when they are all of them, as an import places them.
                var match = known[album];
                var ordered = tracks.All(track => track.Tags?.Track is not null)
                    ? tracks.OrderBy(track => track.Tags!.Disc ?? 1).ThenBy(track => track.Tags!.Track).ToArray()
                    : [.. tracks.OrderBy(track => ImportPlacementLogic.NaturalKey(track.RelativePath), StringComparer.OrdinalIgnoreCase)];
                var units = ordered.Length == match.TrackCount;
                for (var index = 0; index < ordered.Length; index++)
                {
                    var assignment = new StoredFileAssignmentData(ordered[index].StoredFileId, match.WorkId, null, units ? WorkUnitType.Track : null, units ? index + 1 : null, null, null, false);
                    try
                    {
                        await context.ExecuteBatchAsync((ctx, token) => ctx.AssignStoredFileAsync(assignment, token), ct);
                    }
                    catch (DataValidationException)
                    {
                        // A file assigned meanwhile keeps its assignment.
                    }
                }
            }
        }

        // An unreachable file reads as untagged.
        private static async Task<AudioTags?> ReadTagsAsync(StorageFiles storage, string path, CancellationToken ct)
        {
            try
            {
                await using var stream = await storage.OpenReadAsync(path, ct);
                return await Task.Run(() => AudioTagReader.Read(StoragePath.Name(path), stream), ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NasMountException)
            {
                return null;
            }
        }

        // A folder belongs to the album every one of its files matches.
        private async Task FingerprintAsync(List<(long StoredFileId, string RelativePath)> untagged, StorageFiles storage, string directory, string key,
            Dictionary<string, List<(long StoredFileId, string RelativePath, AudioTags? Tags)>> albums, CancellationToken ct)
        {
            var budget = FingerprintsPerScan;
            foreach (var folder in untagged.GroupBy(file => file.RelativePath[..Math.Max(0, file.RelativePath.LastIndexOf('/'))], StringComparer.Ordinal))
            {
                var files = folder.ToArray();
                if ((budget -= files.Length) < 0)
                {
                    return;
                }

                var matches = new List<IReadOnlyList<string>>();
                foreach (var file in files)
                {
                    try
                    {
                        var path = StoragePath.Combine(directory, file.RelativePath);
                        AudioFingerprint fingerprint;
                        if (storage is LocalStorageFiles local)
                        {
                            fingerprint = await fingerprinter.FingerprintAsync(local.ResolveFile(path), ct);
                        }
                        else
                        {
                            await using var audio = await storage.OpenReadAsync(path, ct);
                            fingerprint = await fingerprinter.FingerprintAsync(audio, ct);
                        }
                        matches.Add(await acoustId.ReadReleaseGroupsAsync(key, fingerprint.DurationSeconds, fingerprint.Value, ct));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or ProviderUnavailableException or IOException or UnauthorizedAccessException or NasMountException)
                    {
                        matches.Add([]);
                    }

                    if (MusicFingerprintLogic.Album(matches) is null)
                    {
                        break;
                    }
                }

                if (MusicFingerprintLogic.Album(matches) is { } album && matches.Count == files.Length)
                {
                    foreach (var file in files)
                    {
                        Add(albums, album, (file.StoredFileId, file.RelativePath, null));
                    }
                }
            }
        }

        // AcoustID takes part once its key is stored and it is switched on.
        private async Task<string?> AcoustIdKeyAsync(LogicContext context, CancellationToken ct)
        {
            var credential = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadProviderCredentialAsync(new ProviderParameters("acoustid"), token), ct);
            if (credential is not { IsEnabled: true, ProtectedSecret: { } secret })
            {
                return null;
            }

            try
            {
                var clear = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose).Unprotect(secret);
                try
                {
                    return Encoding.UTF8.GetString(clear).Trim();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                }
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        private static void Add(Dictionary<string, List<(long StoredFileId, string RelativePath, AudioTags? Tags)>> albums, string album,
            (long StoredFileId, string RelativePath, AudioTags? Tags) file) =>
            (albums.TryGetValue(album, out var tracks) ? tracks : albums[album] = []).Add(file);
    }
}
