using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class CreateWorkParametersV1 : BaseParameters
    {
    }

    public sealed class ReadWorksParametersV1 : BaseParameters
    {
        public ReadWorksParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class WorkParametersV1 : BaseParameters
    {
        public WorkParametersV1(long workId)
        {
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    public sealed class WorkDetailsV1 : BaseResponse
    {
        public WorkDetailsV1(long id, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, bool isMediaSubtypeManual, string canonicalTitle, int? releaseYear, long version,
            long? wantedItemId = null, string? franchiseName = null, decimal? franchisePosition = null, bool isFranchiseManual = false)
        {
            FranchiseName = franchiseName;
            FranchisePosition = franchisePosition;
            IsFranchiseManual = isFranchiseManual;
            WantedItemId = wantedItemId;
            Id = id;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            IsMediaSubtypeManual = isMediaSubtypeManual;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            Version = version;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        // A manual subtype, also none, is kept by provider refreshes.
        [DataField(typeof(WorkFields), nameof(WorkFields.IsMediaSubtypeManual))]
        public bool IsMediaSubtypeManual { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }

        // Its acquisition target, once it is wanted.
        [DataField(typeof(Fields.Acquisition.WantedItemFields), nameof(Fields.Acquisition.WantedItemFields.Id), false)]
        public long? WantedItemId { get; }

        // Its series and place there; an administrator's choice is kept by imports.
        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Name), false)]
        public string? FranchiseName { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Position), false)]
        public decimal? FranchisePosition { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsFranchiseManual))]
        public bool IsFranchiseManual { get; }
    }

    // A series by name with the Work's place in it; no name leaves the series to the providers again.
    public sealed class UpdateWorkFranchiseDataV1 : BaseData
    {
        public UpdateWorkFranchiseDataV1(string? franchiseName, decimal? franchisePosition)
        {
            FranchiseName = franchiseName;
            FranchisePosition = franchisePosition;
        }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Name), false)]
        public string? FranchiseName { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Position), false)]
        public decimal? FranchisePosition { get; }
    }

    public sealed class CreateWorkDataV1 : BaseData
    {
        public CreateWorkDataV1(MediaType mediaTypeId, string canonicalTitle, int? releaseYear = null, MediaSubtype? mediaSubtypeId = null)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        public override void Validate()
        {
            base.Validate();
            if (!MediaSubtypes.Fits(MediaTypeId, MediaSubtypeId))
            {
                throw new DataValidationException(nameof(MediaSubtypeId), "unknown_catalog_value");
            }
        }
    }

    public sealed class UpdateWorkDataV1 : BaseData
    {
        public UpdateWorkDataV1(string canonicalTitle, long version, int? releaseYear = null, MediaSubtype? mediaSubtypeId = null)
        {
            CanonicalTitle = canonicalTitle;
            Version = version;
            ReleaseYear = releaseYear;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }
}
