using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Statistics.V1
{
    // The last days, or everything with 0.
    public sealed class ReadStatisticsParametersV1 : BaseParameters
    {
        public ReadStatisticsParametersV1(int days = 30)
        {
            Days = days;
        }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Days))]
        public int Days { get; }
    }

    public sealed class ProfileStatisticsV1 : BaseResponse
    {
        public ProfileStatisticsV1(long watchedMs, long sessions, long gamePlayedMs, BaseDataList<MediaTypeCountV1> completed, BaseDataList<WorkTimeV1> topWorks)
        {
            WatchedMs = watchedMs;
            Sessions = sessions;
            GamePlayedMs = gamePlayedMs;
            Completed = completed;
            TopWorks = topWorks;
        }

        // Time in the player, pauses included.
        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.DurationMs))]
        public long WatchedMs { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long Sessions { get; }

        // Retro games, all time.
        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.DurationMs))]
        public long GamePlayedMs { get; }

        public BaseDataList<MediaTypeCountV1> Completed { get; }

        public BaseDataList<WorkTimeV1> TopWorks { get; }
    }

    public sealed class MediaTypeCountV1 : BaseResponse
    {
        public MediaTypeCountV1(MediaType mediaTypeId, long count, long bytes = 0)
        {
            MediaTypeId = mediaTypeId;
            Count = count;
            Bytes = bytes;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long Count { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Bytes))]
        public long Bytes { get; }
    }

    public sealed class WorkTimeV1 : BaseResponse
    {
        public WorkTimeV1(long workId, string title, long durationMs)
        {
            WorkId = workId;
            Title = title;
            DurationMs = durationMs;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.DurationMs))]
        public long DurationMs { get; }
    }
}
