using System.Text.RegularExpressions;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Integrations.Data.Usenet;
using Jularr.Integrations.Logic.Usenet;
using Microsoft.AspNetCore.DataProtection;

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
        public UsenetPassResult(UsenetPassState state, int progressPercent, long sizeBytes, long remainingBytes, string? reasonKey = null, string? outputPath = null)
        {
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
    }

    // Jularr's own Usenet downloads: NZB and progress live in staging; a pass downloads until its deadline, then repairs and unpacks.
    internal sealed partial class UsenetJob
    {
        public const string NzbName = "job.nzb";
        public const string ExternalId = "usenet";
        private const string StateName = "job.state";
        private const string FilesFolder = "files";
        private const string OutputFolder = "output";
        private readonly UsenetDownloader _downloader;
        private readonly ArchiveTools _tools;
        private readonly IDataProtector _protector;

        public UsenetJob(UsenetDownloader downloader, ArchiveTools tools, IDataProtectionProvider protection)
        {
            _downloader = downloader;
            _tools = tools;
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

            // Finished articles are remembered; a leading "!" marks a damaged one.
            var statePath = Path.Combine(staging, StateName);
            var state = File.Exists(statePath) ? await File.ReadAllLinesAsync(statePath, ct) : [];
            var finished = state.Select(line => line.TrimStart('!')).ToHashSet(StringComparer.Ordinal);
            var directory = Directory.CreateDirectory(Path.Combine(staging, FilesFolder)).FullName;
            var progress = await _downloader.DownloadAsync(Server(server), files, directory, finished, until, ct);
            await File.AppendAllLinesAsync(statePath, [.. progress.Done, .. progress.Damaged.Select(id => "!" + id)], ct);
            finished.UnionWith(progress.Done);
            finished.UnionWith(progress.Damaged);

            var size = files.Sum(file => file.Bytes);
            var remaining = files.SelectMany(file => file.Segments).Where(segment => !finished.Contains(segment.MessageId)).Sum(segment => segment.Bytes);
            if (remaining > 0)
            {
                return new UsenetPassResult(UsenetPassState.Downloading, size == 0 ? 0 : (int)Math.Clamp((size - remaining) * 100 / size, 0, 99), size, remaining);
            }

            // Damaged articles need the repair files.
            var damaged = state.Any(line => line.StartsWith('!')) || progress.Damaged.Count > 0;
            if (damaged)
            {
                var par2 = Directory.EnumerateFiles(directory, "*.par2").Where(path => !Path.GetFileName(path).Contains(".vol", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                if (par2 is null)
                {
                    return new UsenetPassResult(UsenetPassState.Failed, 100, size, 0, "usenet_incomplete");
                }

                if (!_tools.CanRepair)
                {
                    return new UsenetPassResult(UsenetPassState.Waiting, 100, size, 0, "usenet_repair_missing");
                }

                if (!await _tools.RepairAsync(par2, ct))
                {
                    return new UsenetPassResult(UsenetPassState.Failed, 100, size, 0, "usenet_repair_failed");
                }
            }

            // Archives are unpacked from their first volume; plain releases are imported as they are.
            var archives = Directory.EnumerateFiles(directory).Where(IsFirstVolume).ToArray();
            if (archives.Length == 0)
            {
                return new UsenetPassResult(UsenetPassState.Completed, 100, size, 0, outputPath: directory);
            }

            if (!_tools.CanExtract)
            {
                return new UsenetPassResult(UsenetPassState.Waiting, 100, size, 0, "usenet_extractor_missing");
            }

            var output = Directory.CreateDirectory(Path.Combine(staging, OutputFolder)).FullName;
            foreach (var archive in archives)
            {
                if (!await _tools.ExtractAsync(archive, output, ct))
                {
                    return new UsenetPassResult(UsenetPassState.Failed, 100, size, 0, "usenet_extract_failed");
                }
            }

            return new UsenetPassResult(UsenetPassState.Completed, 100, size, 0, outputPath: output);
        }

        public Task TestAsync(AcquisitionConnectionData server, CancellationToken ct) => _downloader.TestAsync(Server(server), ct);

        private UsenetServer Server(AcquisitionConnectionData connection)
        {
            var address = new Uri(connection.BaseUrl);
            var tls = address.Scheme == "nntps";
            return new UsenetServer(address.Host, address.IsDefaultPort || address.Port <= 0 ? tls ? 563 : 119 : address.Port, tls, connection.Username,
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
