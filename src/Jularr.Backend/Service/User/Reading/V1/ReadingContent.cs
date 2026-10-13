using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Reading.V1
{
    // Prepares books from storage into the shared reading cache; I/O stays outside transactions.
    internal sealed class ReadingContent
    {
        private const long BatchBytes = 8L * 1024 * 1024;
        private readonly StorageOptions _options;
        private readonly TimeProvider _time;

        public ReadingContent(IOptions<StorageOptions> options, TimeProvider time)
        {
            _options = options.Value;
            _time = time;
        }

        public static string SectionPath(int index) => ":section/" + index.ToString("D5", CultureInfo.InvariantCulture);

        public async Task<long> OpenAsync(LogicContext context, ReadingSourceData source, CancellationToken ct)
        {
            // A complete cache needs no storage.
            if (source is { DocumentId: { } cached, IsCached: true })
            {
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.TouchReadingDocumentAsync(new ReadingDocumentIdData(cached), token);
                    return true;
                }, ct);
                return cached;
            }

            if (source.IsMissing)
            {
                throw new ServiceUnavailableException("reading_unavailable");
            }

            FileStream file;
            try
            {
                var connection = _options.GetConnectionPath(source.StorageProtocolTypeId, source.StorageMountId, source.SharePath ?? "");
                var directory = StorageFileSystem.ResolveDirectory(connection, source.RootRelativePath);
                file = new FileStream(StorageFileSystem.ResolveFile(directory, source.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                // Offline storage is not a missing book.
                throw new ServiceUnavailableException("reading_unavailable");
            }

            await using (file)
            {
                var hash = await SHA256.HashDataAsync(file, ct);
                file.Position = 0;
                EpubPackage package;
                ZipArchive archive;
                try
                {
                    archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
                    package = EpubReader.Read(archive);
                }
                catch (InvalidDataException)
                {
                    throw new ServiceUnavailableException("reading_invalid_file");
                }

                using (archive)
                {
                    var save = new ReadingDocumentSaveData(source.StoredFileId, source.SizeBytes, source.ModifiedAt, hash, ReadingHtml.AnalyzerVersion, ReadingFormatType.Epub, package.Title,
                        package.Language, "{\"Sections\":[],\"Toc\":[]}", 0);
                    var state = await context.ExecuteBatchAsync((ctx, token) => ctx.CreateReadingDocumentAsync(save, token), ct);
                    if (!state.IsCached)
                    {
                        await PrepareAsync(context, state.DocumentId, archive, package, ct);
                    }

                    return state.DocumentId;
                }
            }
        }

        private async Task PrepareAsync(LogicContext context, long documentId, ZipArchive archive, EpubPackage package, CancellationToken ct)
        {
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.ClearReadingResourcesAsync(new ReadingDocumentIdData(documentId), token);
                return true;
            }, ct);

            // Section by section in bounded batches.
            var indexes = package.Spine.Select((item, index) => (item.Path, index)).ToDictionary(pair => pair.Path, pair => pair.index, StringComparer.Ordinal);
            var sections = new List<ReadingStructureSection>();
            var images = new HashSet<string>(StringComparer.Ordinal);
            var batch = new List<ReadingResourceData>();
            long batchBytes = 0;
            for (var index = 0; index < package.Spine.Count; index++)
            {
                var item = package.Spine[index];
                var prepared = ReadingHtml.Prepare(EpubReader.ReadText(archive.GetEntry(item.Path)!), item.Path, documentId, indexes, package.Manifest);
                var title = package.Toc.FirstOrDefault(entry => entry.Path == item.Path)?.Title;
                sections.Add(new ReadingStructureSection(item.Path, title, prepared.Characters, prepared.Blocks));
                await AddAsync(new ReadingResourceData(documentId, SectionPath(index), "text/html; charset=utf-8", Encoding.UTF8.GetBytes(prepared.Html)));
                foreach (var image in prepared.Resources.Where(images.Add))
                {
                    if (archive.GetEntry(image) is { } entry)
                    {
                        await AddAsync(new ReadingResourceData(documentId, image, package.Manifest[image], EpubReader.ReadBytes(entry)));
                    }
                }
            }

            await FlushAsync();
            var toc = package.Toc.Where(entry => indexes.ContainsKey(entry.Path)).Select(entry => new ReadingStructureEntry(entry.Title, indexes[entry.Path], entry.Level, entry.Anchor)).ToArray();
            var structure = JsonSerializer.Serialize(new ReadingStructure([.. sections], toc));
            var complete = new ReadingDocumentCompleteData(documentId, package.Title, package.Language, structure, sections.Sum(section => section.Characters));
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.CompleteReadingDocumentAsync(complete, token);
                await ctx.EvictReadingCacheAsync(await ReadBudgetAsync(ctx, token), token);
                return true;
            }, ct);

            async Task AddAsync(ReadingResourceData resource)
            {
                batch.Add(resource);
                batchBytes += resource.Bytes.Length;
                if (batchBytes >= BatchBytes)
                {
                    await FlushAsync();
                }
            }

            async Task FlushAsync()
            {
                if (batch.Count == 0)
                {
                    return;
                }

                var rows = batch.ToArray();
                batch.Clear();
                batchBytes = 0;
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    foreach (var row in rows)
                    {
                        await ctx.SaveReadingResourceAsync(row, token);
                    }

                    return true;
                }, ct);
            }
        }

        private async Task<ReadingCacheBudgetData> ReadBudgetAsync(LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."ReadingCacheBytes" AS "CacheBudgetBytes",
                    settings."ReadingCacheWorks" AS "CacheBudgetWorks",
                    now() - interval '14 days' AS "ProtectedSince"
                FROM
                    "InstanceSettings" AS settings;
                """;
            return await ctx.ReadSql.ReadOptionalAsync<ReadingCacheBudgetData>(sql, ct)
                ?? new ReadingCacheBudgetData(2L * 1024 * 1024 * 1024, 100, _time.GetUtcNow().UtcDateTime - ReadingLogic.ProtectedFor);
        }
    }
}
