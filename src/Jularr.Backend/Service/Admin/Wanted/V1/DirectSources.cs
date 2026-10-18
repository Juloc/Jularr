using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.AnnasArchive;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Gutenberg;
using Jularr.Integrations.Data.Opds;
using Jularr.Integrations.Logic.AnnasArchive;
using Jularr.Integrations.Logic.Gutenberg;
using Jularr.Integrations.Logic.Opds;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    // Sources that search and serve files themselves: Gutenberg, OPDS catalogs and Anna's Archive.
    internal sealed class DirectSources
    {
        private readonly GutenbergClient _gutenberg;
        private readonly OpdsClient _opds;
        private readonly AnnasArchiveClient _annas;
        private readonly IDataProtector _protector;

        public DirectSources(GutenbergClient gutenberg, OpdsClient opds, AnnasArchiveClient annas, IDataProtectionProvider protection)
        {
            _gutenberg = gutenberg;
            _opds = opds;
            _annas = annas;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        // The download waits for a person to pass the source's own check and paste the file link.
        public const string ManualLinkKey = "source_manual_link";

        // The longest waiting page Jularr sits through itself.
        private static readonly TimeSpan SlowWait = TimeSpan.FromSeconds(90);

        public static bool IsDirect(AcquisitionConnectionType type) => type is AcquisitionConnectionType.Gutenberg or AcquisitionConnectionType.Opds or AcquisitionConnectionType.AnnasArchive;

        // Offer ids are "<source>:<key>:<format>"; the format names the stored file.
        public static bool IsDirectOffer(string? guid) => guid is not null && (guid.StartsWith("gutenberg:", StringComparison.Ordinal) || guid.StartsWith("opds:", StringComparison.Ordinal)
            || guid.StartsWith("annas:", StringComparison.Ordinal));

        public static string Format(string externalId) => externalId.Split(':').Last() is { Length: > 0 and <= 5 } format && format.All(char.IsAsciiLetterOrDigit) ? format : "epub";

        public async Task<IReadOnlyList<ReleaseOffer>?> SearchAsync(AcquisitionConnectionData source, WantedTargetData target, CancellationToken ct)
        {
            // Named like book releases so the shared judge decides; an edition in the requested language is asked for by its title, Anna's Archive by its ISBN.
            var query = string.Join(' ', new[] { target.EditionTitles.FirstOrDefault() ?? target.CanonicalTitle, BookReleaseJudge.Authors(target.Author).FirstOrDefault() }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            try
            {
                switch (source.AcquisitionConnectionTypeId)
                {
                    case AcquisitionConnectionType.Gutenberg:
                        return [.. (await _gutenberg.SearchAsync(Catalog(source), query, ct)).Select(book => Offer(source, "gutenberg:" + Text(book.Id) + ":epub", book.Authors, book.Title,
                            book.Languages.FirstOrDefault(), "epub", GutenbergClient.EpubUri(book.Id), null))];
                    case AcquisitionConnectionType.Opds:
                        return [.. (await _opds.SearchAsync(Opds(source), query, ct)).SelectMany(book => book.Links.Select(link =>
                            Offer(source, "opds:" + Hash(book.Id) + ":" + link.Format, book.Authors, book.Title, book.Language, link.Format, link.Uri, null)))];
                    case AcquisitionConnectionType.AnnasArchive:
                        var site = Catalog(source);
                        return [.. (await _annas.SearchAsync(site, target.Isbns.FirstOrDefault() ?? query, ct)).Select(record => Offer(source, "annas:" + record.Md5 + ":" + record.Extension,
                            record.Author is null ? [] : [record.Author], record.Title, record.Language, record.Extension, AnnasArchiveClient.RecordUri(site, record.Md5), record.SizeBytes))];
                    default:
                        return [];
                }
            }
            catch (Exception exception) when (exception is GutenbergException or OpdsException or AnnasArchiveException)
            {
                return null;
            }
        }

        public async Task<string?> TestAsync(AcquisitionConnectionData source, CancellationToken ct)
        {
            // A search that answers proves the source; null means healthy.
            var probe = new WantedTargetData(0, 0, Data.Enums.Media.MediaType.Book, null, "Austen", null, [], null, null, null, [], false, false, false, 0, [], [], null, null, null);
            return await SearchAsync(source, probe, ct) is null ? "acquisition_unavailable" : null;
        }

        public async Task DownloadAsync(AcquisitionConnectionData source, DownloadData download, string destination, DownloadStoragePaths paths, CancellationToken ct)
        {
            var directory = Path.GetDirectoryName(destination)!;
            IDisposable Reserve(long bytes) => paths.ReserveWrite(download.OperationId, directory, bytes);
            if (!Uri.TryCreate(download.SourceUrl, UriKind.Absolute, out var address))
            {
                throw new DirectSourceException("acquisition_grab_failed");
            }

            try
            {
                switch (source.AcquisitionConnectionTypeId)
                {
                    case AcquisitionConnectionType.Gutenberg:
                        var id = download.ExternalId?.Split(':') is [_, var key, ..] && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var bookId)
                            ? bookId
                            : throw new DirectSourceException("acquisition_grab_failed");
                        await _gutenberg.DownloadAsync(id, destination, ct, paths.Settings.MaxJobBytes, Reserve);
                        break;
                    case AcquisitionConnectionType.Opds:
                        await _opds.DownloadAsync(Opds(source), address, destination, ct, paths.Settings.MaxJobBytes, Reserve);
                        break;
                    case AcquisitionConnectionType.AnnasArchive:
                        var site = Catalog(source);
                        var md5 = AnnasArchiveClient.ReadMd5(address) ?? throw new DirectSourceException("acquisition_grab_failed");
                        if (source.ProtectedApiKey is { } memberKey)
                        {
                            await _annas.DownloadAsync(site, AcquisitionConnectionInput.Unprotect(_protector, memberKey), md5, destination, ct, paths.Settings.MaxJobBytes, Reserve);
                            break;
                        }

                        // Without a key: the person's file link, else the slow download; checks, long waits and dead links go to a person.
                        try
                        {
                            if (download.SourceLinkUrl is { } link)
                            {
                                await _annas.DownloadFileAsync(new Uri(link), destination, ct, paths.Settings.MaxJobBytes, Reserve);
                            }
                            else
                            {
                                await _annas.SlowDownloadAsync(site, md5, destination, SlowWait, ct, paths.Settings.MaxJobBytes, Reserve);
                            }
                        }
                        catch (AnnasArchiveException exception) when (download.SourceLinkUrl is not null || exception.Failure is AnnasArchiveFailure.Gate or AnnasArchiveFailure.WaitTooLong)
                        {
                            throw new DirectSourceException(ManualLinkKey, AnnasArchiveClient.SlowPageUri(site, md5));
                        }

                        break;
                    default:
                        throw new DirectSourceException("acquisition_grab_failed");
                }
            }
            catch (DirectDownloadException exception)
            {
                if (exception.Failure == DirectDownloadFailure.TooLarge) throw new ServiceUnavailableException("download_size_limit");
                throw new DirectSourceException(exception.Failure switch
                {
                    DirectDownloadFailure.Gate => "source_gate",
                    DirectDownloadFailure.Unauthorized => "acquisition_authentication_failed",
                    _ => "acquisition_unavailable"
                });
            }
            catch (AnnasArchiveException exception)
            {
                throw new DirectSourceException(exception.Failure switch
                {
                    AnnasArchiveFailure.Gate => "source_gate",
                    AnnasArchiveFailure.Refused => "acquisition_authentication_failed",
                    _ => "acquisition_unavailable"
                });
            }
            catch (Exception exception) when (exception is GutenbergException or OpdsException)
            {
                throw new DirectSourceException("acquisition_unavailable");
            }
        }

        private static ReleaseOffer Offer(AcquisitionConnectionData source, string guid, string[] authors, string title, string? language, string format, Uri download, long? sizeBytes)
        {
            var name = (authors.Length > 0 ? string.Join(", ", authors) + " - " : "") + title + (language is { Length: > 0 } ? " (" + language.ToUpperInvariant() + ")" : "") + " (" + format + ")";
            return new ReleaseOffer(name.Length <= 1000 ? name : name[..1000], source.Id, source.Name, source.Priority, guid, download, download.ToString(), sizeBytes, null, ["title"]);
        }

        private OpdsConnection Opds(AcquisitionConnectionData source) =>
            new(new Uri(source.HttpAddress), source.Username, source.ProtectedApiKey is null ? null : AcquisitionConnectionInput.Unprotect(_protector, source.ProtectedApiKey));

        private static Uri Catalog(AcquisitionConnectionData source) => new(source.HttpAddress.TrimEnd('/') + "/");

        private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
    }

    internal sealed class DirectSourceException(string errorKey, Uri? manualUrl = null) : Exception("The direct source failed.")
    {
        public string ErrorKey { get; } = errorKey;

        // The page a person opens to pass the source's check.
        public Uri? ManualUrl { get; } = manualUrl;
    }
}
