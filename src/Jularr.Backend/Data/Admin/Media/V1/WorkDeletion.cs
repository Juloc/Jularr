using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    // Sonarr's dialog: the title leaves the library, its files go too when asked, and an exclusion keeps a request from bringing it back.
    public sealed class DeleteWorkDataV1 : BaseData
    {
        public DeleteWorkDataV1(bool deletesFiles, bool addsExclusion)
        {
            DeletesFiles = deletesFiles;
            AddsExclusion = addsExclusion;
        }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.DeletesFiles))]
        public bool DeletesFiles { get; }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.AddsExclusion))]
        public bool AddsExclusion { get; }
    }

    public sealed class ReadWorkExclusionsParametersV1 : BaseParameters
    {
        public ReadWorkExclusionsParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class WorkExclusionParametersV1 : BaseParameters
    {
        public WorkExclusionParametersV1(long workExclusionId)
        {
            WorkExclusionId = workExclusionId;
        }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.Id))]
        public long WorkExclusionId { get; }
    }

    // A title that was deleted with an exclusion; its provider identities cannot be requested again until the exclusion is removed.
    public sealed class WorkExclusionV1 : BaseResponse
    {
        public WorkExclusionV1(long id, MediaType mediaTypeId, string title, int? releaseYear, DateTime createdAt)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            Title = title;
            ReleaseYear = releaseYear;
            CreatedAt = createdAt;
        }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.Title))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.CreatedAt))]
        public DateTime CreatedAt { get; }
    }
}
