using System.Text.RegularExpressions;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Usenet;
using Jularr.Integrations.Logic.Usenet;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    internal enum UsenetPassState : byte
    {
        Downloading,
        Completed,
        Failed,
        Waiting
    }

    internal sealed class UsenetPassResult
    {
        public UsenetPassResult(UsenetPassState state, int progressPercent, long sizeBytes, long remainingBytes, string? reasonKey = null, string? outputPath = null, bool progressed = true)
        {
            Progressed = progressed;
            State = state;
            ProgressPercent = progressPercent;
            SizeBytes = sizeBytes;
            RemainingBytes = remainingBytes;
            ReasonKey = reasonKey;
            OutputPath = outputPath;
        }

        public UsenetPassState State { get; }
        public int ProgressPercent { get; }
        public long SizeBytes { get; }
        public long RemainingBytes { get; }

        // Why a job failed or waits.
        public string? ReasonKey { get; }

        // The folder the import reads.
        public string? OutputPath { get; }

        // False when the pass got nothing new; the worker then rests.
        public bool Progressed { get; }
    }

    // Jularr's own Usenet downloads: NZB and progress live in staging; a pass downloads until its deadline, then repairs and unpacks.
    internal sealed partial class UsenetJob
    {
        public const string NzbName = "job.nzb";
        public const string ExternalId = "usenet";
        private const string StateName = "job.state";
        private const string RepairedName = "job.repaired";
        private const string UnpackedName = "job.unpacked";
        private const string FilesFolder = "files";
        private const string OutputFolder = "output";
        private readonly UsenetDownloader _downloader;
        private readonly ArchiveTools _tools;
        private readonly IDataProtector _protector;
        private readonly StorageOptions _storage;

        public UsenetJob(UsenetDownloader downloader, ArchiveTools tools, IDataProtectionProvider protection, IOptions<StorageOptions> storage)
        {
            _downloader = downloader;
            _tools = tools;
            _storage = storage.Value;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        [GeneratedRegex(@"\.part0*(\d+)\.rar$", RegexOptions.IgnoreCase)]
        private static partial Regex RarPart();

        public async Task<UsenetPassResult> RunAsync(AcquisitionConnectionData server, string staging, DateTimeOffset until, CancellationToken ct)
        {
            IReadOnlyList<NzbFile> files;
            await using (var nzb = File.OpenRead(Path.Combine(staging, NzbName)))
            {
                files = NzbReader.Read(nzb);
            }

            // Check the NZB itself; indexer sizes are hints, not a trusted disk bound.
            // Unknown or overflowing sizes are not allowed to consume unbounded storage.
            var announced = 0L;
            foreach (var file in files)
            {
                foreach (var segment in file.Segments)
                {
                    if (_storage.UsenetMaxJobBytes <= 0 || segment.Bytes <= 0 || segment.Bytes > _storage.UsenetMaxJobBytes - announced)
                    {
                        return new UsenetPassResult(UsenetPassState.Waiting, 0, 0, 0, "usenet_disk_budget", progressed: false);
                    }

                    announced += segment.Bytes;
                }
            }

            // Finished articles are remembered; a leading "!" marks a damaged one.
            var statePath = Path.Combine(staging, StateName);
            var state = File.Exists(statePath) ? await File.ReadAllLinesAsync(statePath, ct) : [];
            var finished = state.Select(line => line.TrimStart('!')).ToHashSet(StringComparer.Ordinal);
            var directory = Directory.CreateDirectory(Path.Combine(staging, FilesFolder)).FullName;
            UsenetProgress progress;
            try
            {
                progress = await _downloader.DownloadAsync(Server(server), files, directory, finished, until, _storage.UsenetMaxJobBytes, ct);
            }
            catch (UsenetStorageBudgetException)
            {
                return new UsenetPassResult(UsenetPassState.Waiting, 0, announced, 0, "usenet_disk_budget", progressed: false);
            }

            await File.AppendAllLinesAsync(statePath, [.. progress.Done, .. progress.Damaged.Select(id => "!" + id)], ct);
            finished.UnionWith(progress.Done);
            finished.UnionWith(progress.Damaged);

            // Completion counts articles; announced sizes only show the progress.
            var size = files.Sum(file => file.Bytes);
            var open = files.SelectMany(file => file.Segments).Where(segment => !finished.Contains(segment.MessageId)).ToArray();
            if (open.Length > 0)
            {
                var remaining = open.Sum(segment => segment.Bytes);
                return new UsenetPassResult(UsenetPassState.Downloading, size == 0 ? 0 : (int)Math.Clamp((size - remaining) * 100 / size, 0, 99), size, remaining,
                    progressed: progress.Done.Count + progress.Damaged.Count > 0);
            }

            // Damaged articles need the repair files; a finished step is not repeated.
            var damaged = state.Any(line => line.StartsWith('!')) || progress.Damaged.Count > 0;
            if (damaged && !File.Exists(Path.Combine(staging, RepairedName)))
            {
                var par2 = Directory.EnumerateFiles(directory, "*.par2").OrderBy(path => path.Length).FirstOrDefault();
                if (par2 is null)
                {
                    return new UsenetPassResult(UsenetPassState.Failed, 100, size, 0, "usenet_incomplete");
                }

                if (!_tools.CanRepair)
                {
                    return new UsenetPassResult(UsenetPassState.Waiting, 100, size, 0, "usenet_repair_missing", progressed: false);
                }

                if (!await _tools.RepairAsync(par2, ct))
                {
                    return new UsenetPassResult(UsenetPassState.Failed, 100, size, 0, "usenet_repair_failed");
                }

                await File.WriteAllTextAsync(Path.Combine(staging, RepairedName), "", ct);
            }

            // Archives are unpacked from their first volume; plain releases are imported as they are.
            var output = Path.Combine(staging, OutputFolder);
            if (File.Exists(Path.Combine(staging, UnpackedName)))
            {
                return new UsenetPassResult(UsenetPassState.Completed, 100, size, 0, outputPath: output);
            }

            var archives = Directory.EnumerateFiles(directory).Where(IsFirstVolume).ToArray();
            if (archives.Length == 0)
            {
                return new UsenetPassResult(UsenetPassState.Completed, 100, size, 0, outputPath: directory);
            }

            if (!_tools.CanExtract)
            {
                return new UsenetPassResult(UsenetPassState.Waiting, 100, size, 0, "usenet_extractor_missing", progressed: false);
            }

            // Require space on the actual mounted download filesystem, not the VM root.
            // Keep extraction unopened when free space cannot be verified.
            var unpackBytes = Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length);
            if (StorageFileSystem.AvailableFreeSpace(directory) is not { } available || available < unpackBytes + unpackBytes / 10)
            {
                return new UsenetPassResult(UsenetPassState.Waiting, 100, size, 0, "usenet_disk_full", progressed: false);
            }

            Directory.CreateDirectory(output);

            foreach (var archive in archives)
            {
                if (!await _tools.ExtractAsync(archive, output, ct))
                {
                    return new UsenetPassResult(UsenetPassState.Failed, 100, size, 0, "usenet_extract_failed");
                }
            }

            await File.WriteAllTextAsync(Path.Combine(staging, UnpackedName), "", ct);
            return new UsenetPassResult(UsenetPassState.Completed, 100, size, 0, outputPath: output);
        }

        public Task TestAsync(AcquisitionConnectionData server, CancellationToken ct) => _downloader.TestAsync(Server(server), ct);

        // Without a port a news server answers on 563 with SSL and on 119 without.
        private UsenetServer Server(AcquisitionConnectionData connection)
        {
            var tls = connection.UseSsl ?? true;
            return new UsenetServer(connection.Host!, connection.Port ?? (tls ? 563 : 119), tls, connection.Username,
                connection.ProtectedApiKey is null ? null : AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedApiKey), connection.MaxConnections ?? 8);
        }

        private static bool IsFirstVolume(string path)
        {
            var name = Path.GetFileName(path);
            if (RarPart().Match(name) is { Success: true } part)
            {
                return part.Groups[1].Value == "1";
            }

            return name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".7z.001", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        }
    }
}
