using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Logic.AniList;

namespace Jularr.Backend.Logic.AniList
{
    // How a profile's AniList list and its progress in Jularr meet. Both sides only move forward: what AniList counts as watched or read is taken over,
    // what the profile finished is sent when it is further, and neither takes the other back. The further progress wins when both moved.
    internal static class AniListSyncRules
    {
        public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(["CURRENT", "PLANNING", "COMPLETED", "DROPPED", "PAUSED", "REPEATING"], StringComparer.Ordinal);

        private static readonly TimeSpan BaseBackoff = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(6);

        // Episodes or volumes finished in a row from the first one on; a gap ends the row.
        public static int FinishedInARow(IReadOnlyList<AniListUnitData> units)
        {
            var finished = units.GroupBy(unit => unit.Number).ToDictionary(group => group.Key, group => group.Any(unit => unit.IsCompleted));
            var number = 1;
            while (finished.TryGetValue(number, out var isFinished) && isFinished)
            {
                number++;
            }

            return number - 1;
        }

        // The units AniList counts as done that are not done in Jularr yet; a completed entry covers everything it names.
        public static IReadOnlyList<AniListUnitData> PlanImport(AniListEntryData entry, IReadOnlyList<AniListUnitData> units)
        {
            var reached = entry.Status == "COMPLETED" ? int.MaxValue : entry.IsManga ? entry.ProgressVolumes : entry.Progress;
            return [.. units.Where(unit => !unit.IsCompleted && unit.Number <= reached)];
        }

        // Progress to send, or null when nothing is to be sent: only entries the person tracks (or has planned), only further than AniList has, only progress made since sending was switched on,
        // and never the last episode or volume, which AniList closes itself (it also dates the end).
        public static AniListPush? PlanPush(AniListEntryData entry, IReadOnlyList<AniListUnitData> units, DateTime pushEnabledAt)
        {
            if (entry.Status is not ("CURRENT" or "REPEATING" or "PLANNING"))
            {
                return null;
            }

            var local = FinishedInARow(units);
            var remote = entry.IsManga ? entry.ProgressVolumes : entry.Progress;
            var total = entry.IsManga ? entry.TotalVolumes : entry.TotalCount;
            if (local <= remote || total is { } count && local >= count)
            {
                return null;
            }

            // The further episodes were finished after sending was switched on.
            var recent = units.Any(unit => unit.IsCompleted && unit.Number > remote && unit.Number <= local && unit.CompletedAt is { } at && at >= pushEnabledAt);
            return recent ? new AniListPush(entry.MediaId, entry.ListEntryId, entry.IsManga, local, entry.Status == "PLANNING") : null;
        }

        // Where the profile's progress stands against the entry: a completed entry counts as everything it names.
        public static AniListSyncStateType StateOf(string status, int remote, int? total, int local)
        {
            var reached = status == "COMPLETED" && total is { } count ? Math.Max(remote, count) : remote;
            return local == reached ? AniListSyncStateType.Synchronized : local > reached ? AniListSyncStateType.LocalAhead : AniListSyncStateType.AniListAhead;
        }

        // Why a difference stays instead of going at the next pass; none while the two agree, and none for what the next pass settles by itself.
        public static AniListSyncProblemType? ProblemOf(AniListSyncStateType state, bool isPushEnabled, bool isExpired, string? failureCode, string? lastError)
        {
            if (state == AniListSyncStateType.Synchronized)
            {
                return null;
            }

            if (isExpired || failureCode == "anilist_authentication_failed")
            {
                return AniListSyncProblemType.SignInNeeded;
            }

            if (failureCode is not null)
            {
                return AniListSyncProblemType.Unreachable;
            }

            if (state != AniListSyncStateType.LocalAhead)
            {
                return null;
            }

            return !isPushEnabled ? AniListSyncProblemType.SendingOff : lastError is not null ? AniListSyncProblemType.SendFailed : null;
        }

        // A failed write waits a minute, then twice as long each time, at most six hours.
        public static TimeSpan RetryDelay(int attempts) =>
            attempts <= 1 ? BaseBackoff : TimeSpan.FromTicks(Math.Min(MaximumBackoff.Ticks, BaseBackoff.Ticks << Math.Min(attempts - 1, 20)));
    }

    // One progress write: the number of episodes (or volumes) finished, and whether it starts a planned entry.
    internal sealed class AniListPush
    {
        public AniListPush(int mediaId, long listEntryId, bool isVolume, int progress, bool startsTracking)
        {
            MediaId = mediaId;
            ListEntryId = listEntryId;
            IsVolume = isVolume;
            Progress = progress;
            StartsTracking = startsTracking;
        }

        public int MediaId { get; }
        public long ListEntryId { get; }
        public bool IsVolume { get; }
        public int Progress { get; }
        public bool StartsTracking { get; }
    }
}
