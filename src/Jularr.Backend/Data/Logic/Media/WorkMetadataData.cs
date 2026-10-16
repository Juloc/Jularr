using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class WorkRematchData : BaseData
    {
        public WorkRematchData(long id, string @namespace, string value, string canonicalTitle, int? releaseYear, long version)
        {
            Id = id;
            Namespace = @namespace;
            Value = value;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            Version = version;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId))]
        public string Value { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }

    internal sealed class WorkMetadataData : BaseData
    {
        public WorkMetadataData(MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string @namespace, string value, string canonicalTitle, string? originalTitle, int? releaseYear, string externalIdentities,
            string units, string facts, string providerKey, DateOnly? releasedOn)
        {
            ReleasedOn = releasedOn;
            ProviderKey = providerKey;
            Facts = facts;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
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

        // A provider classification never clears an existing or manual one.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

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

        // The provider's date wins; none keeps the stored one.
        [DataField(typeof(WorkFields), nameof(WorkFields.ReleasedOn))]
        public DateOnly? ReleasedOn { get; }

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

        // JSON rows of Language, Region, Title and Overview in other languages; each enabled language takes its best match.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Translations))]
        public string Translations { get; init; } = "[]";

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.OriginalLanguage))]
        public string? OriginalLanguage { get; init; }
    }

    // A movie's length; a provider that knows none keeps the stored one.
    internal sealed class WorkRuntimeData : BaseData
    {
        public WorkRuntimeData(long id, int? runtimeMinutes)
        {
            Id = id;
            RuntimeMinutes = runtimeMinutes;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.RuntimeMinutes))]
        public int? RuntimeMinutes { get; }
    }

    // A book's catalog editions, found again by language and format.
    internal sealed class WorkEditionsData : BaseData
    {
        public WorkEditionsData(long id, string editions)
        {
            Id = id;
            Editions = editions;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        // JSON rows of LanguageCode, EditionFormatId, Title, Publisher, ReleaseYear and Identities (Namespace, Value).
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Editions))]
        public string Editions { get; }
    }

    // The provider's people of a Work, replacing what its last import stored.
    internal sealed class WorkPeopleData : BaseData
    {
        public WorkPeopleData(long id, string @namespace, string people)
        {
            Id = id;
            Namespace = @namespace;
            People = people;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        // The provider whose person ids these are.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.PersonNamespace))]
        public string Namespace { get; }

        // JSON rows of ExternalId, Name, ImageUrl, PersonRoleId, Position, Credit and, for a unit, WorkUnitTypeId, ParentNumber and Number.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.People))]
        public string People { get; }
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

    // JSON rows of Namespace and Value.
    internal sealed class WorkIdentityLookupData : BaseData
    {
        public WorkIdentityLookupData(string values)
        {
            Values = values;
        }

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
        public LibraryFilterData(MediaType? mediaTypeId, MediaSubtype? mediaSubtypeId, string? query, LibrarySortType librarySortTypeId = LibrarySortType.RecentlyAdded)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            Query = query;
            LibrarySortTypeId = librarySortTypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId), false)]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Query), false)]
        public string? Query { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.LibrarySortTypeId))]
        public LibrarySortType LibrarySortTypeId { get; }
    }

    // The provider's series of a Work and the Work's place in it, or none.
    internal sealed class WorkFranchiseData : BaseData
    {
        public WorkFranchiseData(long workId, string? @namespace, string? externalId, string? name, decimal? position)
        {
            WorkId = workId;
            Namespace = @namespace;
            ExternalId = externalId;
            Name = name;
            Position = position;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Namespace), false)]
        public string? Namespace { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.ExternalId), false)]
        public string? ExternalId { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Name), false)]
        public string? Name { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Position), false)]
        public decimal? Position { get; }
    }

    // An administrator's series of a Work by name, or none to leave it to the providers.
    internal sealed class WorkFranchiseChoiceData : BaseData
    {
        public WorkFranchiseChoiceData(long workId, string? name, decimal? position)
        {
            WorkId = workId;
            Name = name;
            Position = position;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Name), false)]
        public string? Name { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Position), false)]
        public decimal? Position { get; }
    }
}
