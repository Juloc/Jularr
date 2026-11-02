using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class StatisticsScopeData : BaseData
    {
        public StatisticsScopeData(long profileId, int days)
        {
            ProfileId = profileId;
            Days = days;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Days))]
        public int Days { get; }
    }

    internal sealed class ProfileTotalsData : BaseData
    {
        public ProfileTotalsData(long watchedMs, long sessions, long gamePlayedMs)
        {
            WatchedMs = watchedMs;
            Sessions = sessions;
            GamePlayedMs = gamePlayedMs;
        }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.DurationMs))]
        public long WatchedMs { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long Sessions { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.DurationMs))]
        public long GamePlayedMs { get; }
    }

    internal sealed class SystemTotalsData : BaseData
    {
        public SystemTotalsData(long databaseBytes, long queuedOperations, long failedOperations, long profiles, long storageOffline, long catalogWorks)
        {
            DatabaseBytes = databaseBytes;
            QueuedOperations = queuedOperations;
            FailedOperations = failedOperations;
            Profiles = profiles;
            StorageOffline = storageOffline;
            CatalogWorks = catalogWorks;
        }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Bytes))]
        public long DatabaseBytes { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long QueuedOperations { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long FailedOperations { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long Profiles { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long StorageOffline { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long CatalogWorks { get; }
    }
}
