using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class WorkMetadataData : BaseData
    {
        public WorkMetadataData(MediaType mediaTypeId, string @namespace, string value, string canonicalTitle, string? originalTitle, int? releaseYear, string externalIdentities,
            string units)
        {
            MediaTypeId = mediaTypeId;
            Namespace = @namespace;
            Value = value;
            CanonicalTitle = canonicalTitle;
            OriginalTitle = originalTitle;
            ReleaseYear = releaseYear;
            ExternalIdentities = externalIdentities;
            Units = units;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value))]
        public string Value { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.OriginalTitle))]
        public string? OriginalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalIdentities))]
        public string ExternalIdentities { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Units))]
        public string Units { get; }
    }

    internal sealed class WorkIdentityData : BaseData
    {
        public WorkIdentityData(string @namespace, string value, long workId)
        {
            Namespace = @namespace;
            Value = value;
            WorkId = workId;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value))]
        public string Value { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    internal sealed class MetadataLanguageData : BaseData
    {
        public MetadataLanguageData(string locale)
        {
            Locale = locale;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Locale))]
        public string Locale { get; }
    }

    internal sealed class WorkIdentityLookupData : BaseData
    {
        public WorkIdentityLookupData(string @namespace, string values)
        {
            Namespace = @namespace;
            Values = values;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalIdentities))]
        public string Values { get; }
    }
}
