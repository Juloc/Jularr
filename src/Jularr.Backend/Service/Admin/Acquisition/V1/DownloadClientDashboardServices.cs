using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}/categories")]
    public sealed class UpdateDownloadClientCategoriesV1 : AdminService<AcquisitionConnectionParametersV1, DownloadClientCategoriesV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var values = new (string Kind, string? Category)[]
            {
                ("Movie", Data.Movie),
                ("Tv", Data.Tv),
                ("Anime", Data.Anime),
                ("Book", Data.Book),
                ("Audiobook", Data.Audiobook),
                ("Manga", Data.Manga),
                ("LightNovel", Data.LightNovel),
                ("Music", Data.Music),
                ("Game", Data.Game)
            };
            var json = JsonSerializer.Serialize(values.Where(value => value.Category is not null)
                .ToDictionary(value => value.Kind, value => value.Category!, StringComparer.Ordinal));

            await Context.UpdateDownloadCategoryMapAsync(new AcquisitionConnectionCategoryMapData(Parameters.AcquisitionConnectionId, json), CancellationToken);
            return new UpdateResult(true);
        }
    }

    // Provider inspection is an Execute operation: the DB read commits before external I/O.
    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}/dashboard")]
    public sealed class InspectDownloadClientV1 : AdminService<AcquisitionConnectionParametersV1, NoData>
    {
        private readonly SabnzbdClient _client;
        private readonly IDataProtector _protector;

        internal InspectDownloadClientV1(SabnzbdClient client, IDataProtectionProvider protection)
        {
            _client = client;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DownloadClientDashboardV1>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var client = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(Parameters.AcquisitionConnectionId, ct), CancellationToken);
            if (client.AcquisitionConnectionTypeId != AcquisitionConnectionType.Sabnzbd)
            {
                throw new DataValidationException(nameof(Parameters.AcquisitionConnectionId), "invalid_transition");
            }

            var connection = new SabnzbdConnection(client.HttpAddress, AcquisitionConnectionInput.Unprotect(_protector, client.ProtectedApiKey));
            var snapshot = await _client.ReadConsoleAsync(connection, CancellationToken);
            var categories = await _client.ReadCategoriesAsync(connection, CancellationToken);
            var files = DownloadClientFolderInspector.Inspect(client.LocalPath, snapshot, client.IncompleteLocalPath, client.RemotePath, client.IncompleteRemotePath);

            return new DownloadClientDashboardV1(snapshot.SpeedBytesPerSecond, snapshot.FreeBytes, snapshot.IsPaused,
                snapshot.Queue.Count >= 200, snapshot.History.Count >= 200,
                new BaseDataList<DownloadClientQueueItemV1>([.. snapshot.Queue.Select(job => new DownloadClientQueueItemV1(job.NzoId, TextLimit.Cut(job.Name, 1000)!,
                    job.Status, job.Percentage is { } progress ? (int)Math.Round(Math.Clamp(progress, 0, 100)) : null,
                    job.SizeBytes, job.RemainingBytes, job.TimeLeftSeconds))]),
                new BaseDataList<DownloadClientHistoryItemV1>([.. snapshot.History.Select(job => new DownloadClientHistoryItemV1(job.NzoId,
                    TextLimit.Cut(job.Name, 1000)!, job.Status, TextLimit.Cut(job.StoragePath, 2048), job.IsFailed))]),
                [.. categories.Take(200).Select(category => TextLimit.Cut(category, 64)!)],
                snapshot.TodayBytes, snapshot.WeekBytes, snapshot.MonthBytes, snapshot.TotalBytes,
                [.. snapshot.OrphanFolders.Where(name => name.Length <= 200).Take(200)],
                files.CompletePath, files.IncompletePath,
                files.CompleteAvailable, files.IncompleteAvailable, new BaseDataList<DownloadClientFolderEntryV1>(files.Entries));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}/jobs")]
    public sealed class ManageDownloadClientJobV1 : AdminService<AcquisitionConnectionParametersV1, DownloadClientJobCommandV1>
    {
        private readonly SabnzbdClient _client;
        private readonly IDataProtector _protector;

        internal ManageDownloadClientJobV1(SabnzbdClient client, IDataProtectionProvider protection)
        {
            _client = client;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if ((Data.Action is "cancel" or "removeHistory" or "removeOrphan") && !Data.Confirmed)
            {
                throw new DataValidationException(nameof(Data.Confirmed), "invalid_transition");
            }

            var client = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(Parameters.AcquisitionConnectionId, ct), CancellationToken);
            if (client.AcquisitionConnectionTypeId != AcquisitionConnectionType.Sabnzbd)
            {
                throw new DataValidationException(nameof(Parameters.AcquisitionConnectionId), "invalid_transition");
            }

            var action = Data.Action switch
            {
                "pause" => SabnzbdJobCommand.Pause,
                "resume" => SabnzbdJobCommand.Resume,
                "cancel" => SabnzbdJobCommand.RemoveFromQueue,
                "removeHistory" => SabnzbdJobCommand.RemoveFromHistory,
                "removeOrphan" => SabnzbdJobCommand.RemoveOrphan,
                _ => throw new DataValidationException(nameof(Data.Action), "invalid_type")
            };

            var connection = new SabnzbdConnection(client.HttpAddress, AcquisitionConnectionInput.Unprotect(_protector, client.ProtectedApiKey));
            var snapshot = await _client.ReadConsoleAsync(connection, CancellationToken);
            var present = action switch
            {
                SabnzbdJobCommand.RemoveFromHistory => snapshot.History.Any(job => job.NzoId == Data.JobId),
                SabnzbdJobCommand.RemoveOrphan => snapshot.OrphanFolders.Contains(Data.JobId, StringComparer.Ordinal),
                _ => snapshot.Queue.Any(job => job.NzoId == Data.JobId)
            };
            if (!present)
            {
                throw new DataValidationException(nameof(Data.JobId), "invalid_transition");
            }

            // Cancel removes temporary files; history removal only removes the provider's record.
            if (action == SabnzbdJobCommand.RemoveOrphan)
            {
                await _client.RemoveOrphanAsync(connection, Data.JobId, CancellationToken);
            }
            else
            {
                await _client.ControlJobAsync(connection, Data.JobId, action, CancellationToken);
            }
            return new UpdateResult(true);
        }
    }

    // File inspection never deletes files: queue/history do not prove Jularr import ownership.
    internal static class DownloadClientFolderInspector
    {
        internal sealed record Snapshot(string? CompletePath, string? IncompletePath, bool CompleteAvailable, bool IncompleteAvailable,
            IReadOnlyList<DownloadClientFolderEntryV1> Entries);

        public static Snapshot Inspect(string? mappedPath, SabnzbdConsoleSnapshot snapshot, string? incompleteMappedPath = null,
            string? remoteCompletePath = null, string? remoteIncompletePath = null)
        {
            // Resolve the same remote-to-local mapping as the import pipeline. No path is guessed from a sibling folder.
            static string? Resolve(string? localRoot, string? remoteRoot, string? reportedPath)
            {
                if (string.IsNullOrWhiteSpace(localRoot) || !Path.IsPathFullyQualified(localRoot)
                    || string.IsNullOrWhiteSpace(remoteRoot) || string.IsNullOrWhiteSpace(reportedPath)
                    || ImportPlacementLogic.MapRemotePath(reportedPath, remoteRoot) is not { } relative)
                {
                    return null;
                }

                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
                if (string.Equals(root, Path.GetPathRoot(root), StringComparison.Ordinal)) return null;
                var resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!resolved.Equals(root, comparison) && !resolved.StartsWith(root + Path.DirectorySeparatorChar, comparison)) return null;
                return resolved;
            }

            var complete = Resolve(mappedPath, remoteCompletePath, snapshot.ReportedCompletePath);
            var incomplete = Resolve(incompleteMappedPath, remoteIncompletePath, snapshot.ReportedIncompletePath);

            static bool SafeDirectory(string? path)
            {
                if (path is null) return false;
                try
                {
                    return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }

            var completeAvailable = SafeDirectory(complete);
            var incompleteAvailable = SafeDirectory(incomplete);

            var queue = snapshot.Queue.Select(job => job.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orphans = snapshot.OrphanFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var history = snapshot.History.Select(job => Path.GetFileName(job.StoragePath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = new List<DownloadClientFolderEntryV1>();

            bool Scan(string? path, string folder, bool available)
            {
                if (!available || path is null) return false;
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path).Take(100))
                    {
                        var name = Path.GetFileName(entry);
                        if (name.Length is 0 or > 256) continue;
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var size = (attributes & FileAttributes.Directory) != 0 ? null : (long?)new FileInfo(entry).Length;
                        var assignment = folder == "incomplete" && orphans.Contains(name) ? "orphan"
                            : queue.Contains(name) ? "queue" : history.Contains(name) ? "history"
                            : snapshot.Queue.Count >= 200 || snapshot.History.Count >= 200 ? "unchecked" : "unassigned";
                        entries.Add(new DownloadClientFolderEntryV1(folder, name, size, assignment));
                    }

                    return true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    entries.RemoveAll(entry => entry.Folder == folder);
                    return false;
                }
            }

            completeAvailable = Scan(complete, "complete", completeAvailable);
            incompleteAvailable = Scan(incomplete, "incomplete", incompleteAvailable);
            return new Snapshot(complete, incomplete, completeAvailable, incompleteAvailable, entries);
        }
    }
}
