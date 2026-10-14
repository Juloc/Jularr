using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Calendar.V1
{
    public sealed class ReadCalendarParametersV1 : BaseParameters
    {
        public ReadCalendarParametersV1(DateOnly from, int days = 14)
        {
            From = from;
            Days = days;
        }

        [DataField(typeof(CalendarFields), nameof(CalendarFields.Day))]
        public DateOnly From { get; }

        [DataField(typeof(CalendarFields), nameof(CalendarFields.Days))]
        public int Days { get; }
    }

    public sealed class CalendarEntryV1 : BaseResponse
    {
        public CalendarEntryV1(DateOnly day, long workId, MediaType mediaTypeId, string title, string? posterUrl, WorkUnitType workUnitTypeId, decimal unitNumber, decimal? parentUnitNumber,
            string? unitTitle, bool isAvailable, bool isWatchlisted)
        {
            Day = day;
            WorkId = workId;
            MediaTypeId = mediaTypeId;
            Title = title;
            PosterUrl = posterUrl;
            WorkUnitTypeId = workUnitTypeId;
            UnitNumber = unitNumber;
            ParentUnitNumber = parentUnitNumber;
            UnitTitle = unitTitle;
            IsAvailable = isAvailable;
            IsWatchlisted = isWatchlisted;
        }

        [DataField(typeof(CalendarFields), nameof(CalendarFields.Day))]
        public DateOnly Day { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? PosterUrl { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId))]
        public WorkUnitType WorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber))]
        public decimal UnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? ParentUnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitTitle))]
        public string? UnitTitle { get; }

        // Already in the library.
        [DataField(typeof(CalendarFields), nameof(CalendarFields.IsAvailable))]
        public bool IsAvailable { get; }

        [DataField(typeof(CalendarFields), nameof(CalendarFields.IsWatchlisted))]
        public bool IsWatchlisted { get; }
    }
}
