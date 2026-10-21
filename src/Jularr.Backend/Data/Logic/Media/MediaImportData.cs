using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // A title of the library that one of the identities of a list names, and whether it already has a target to search for.
    internal sealed class MediaImportKnownData : BaseData
    {
        public MediaImportKnownData(string @namespace, string value, long workId, MediaType mediaTypeId, string title, int? releaseYear, bool hasTarget)
        {
            Namespace = @namespace;
            Value = value;
            WorkId = workId;
            MediaTypeId = mediaTypeId;
            Title = title;
            ReleaseYear = releaseYear;
            HasTarget = hasTarget;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value))]
        public string Value { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Flag))]
        public bool HasTarget { get; }
    }

    // The titles of Works, by id.
    internal sealed class MediaImportWorkLookupData : BaseData
    {
        public MediaImportWorkLookupData(long[] workIds)
        {
            WorkIds = workIds;
        }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.WorkIds))]
        public long[] WorkIds { get; }
    }

    internal sealed class MediaImportWorkData : BaseData
    {
        public MediaImportWorkData(long id, string title)
        {
            Id = id;
            Title = title;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }
    }
}
