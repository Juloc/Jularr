using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class WorkMetadataData : BaseData
    {
        public WorkMetadataData(MediaType mediaTypeId, string @namespace, string value, string canonicalTitle, string? originalTitle, int? releaseYear, string externalIdentities,
            string units, string facts, string providerKey)
        {
            ProviderKey = providerKey;
            Facts = facts;
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

        // JSON rows of Key, Value and whether the value is in the instance language.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Facts))]
        public string Facts { get; }

        // Titles and facts belong to this provider.
        [DataField(typeof(ProviderFields), nameof(ProviderFields.Key))]
        public string ProviderKey { get; }
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

    // Viewer state of a provider title that already is a Work.
    internal sealed class DiscoverOverlayData : BaseData
    {
        public DiscoverOverlayData(string value, long workId, MediaStateType mediaStateTypeId, string? plexUrl)
        {
            Value = value;
            WorkId = workId;
            MediaStateTypeId = mediaStateTypeId;
            PlexUrl = plexUrl;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value))]
        public string Value { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.MediaStateTypeId))]
        public MediaStateType MediaStateTypeId { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.Url))]
        public string? PlexUrl { get; }
    }

    internal sealed class LibraryFilterData : BaseData
    {
        public LibraryFilterData(MediaType? mediaTypeId, string? query, LibrarySortType librarySortTypeId = LibrarySortType.RecentlyAdded)
        {
            MediaTypeId = mediaTypeId;
            Query = query;
            LibrarySortTypeId = librarySortTypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Query), false)]
        public string? Query { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.LibrarySortTypeId))]
        public LibrarySortType LibrarySortTypeId { get; }
    }
}
