using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class ReadIdentityReviewsParametersV1 : BaseParameters
    {
        public ReadIdentityReviewsParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    // A Work whose AniList match waits for a person, with its candidates.
    public sealed class IdentityReviewV1 : BaseResponse
    {
        public IdentityReviewV1(long workId, string canonicalTitle, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, int[] candidateIds, string[] candidateLabels)
        {
            WorkId = workId;
            CanonicalTitle = canonicalTitle;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            CandidateIds = candidateIds;
            CandidateLabels = candidateLabels;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.CandidateIds))]
        public int[] CandidateIds { get; }

        // Title, year and format of each candidate, in the same order.
        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.CandidateLabels))]
        public string[] CandidateLabels { get; }
    }

    public sealed class IdentityReviewParametersV1 : BaseParameters
    {
        public IdentityReviewParametersV1(long workId)
        {
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    // Either an AniList entry, for a series the first season's, or the decision that there is none.
    public sealed class ResolveIdentityReviewDataV1 : BaseData
    {
        public ResolveIdentityReviewDataV1(int? aniListId, bool isDismissed = false)
        {
            AniListId = aniListId;
            IsDismissed = isDismissed;
        }

        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.ProviderId), false)]
        public int? AniListId { get; }

        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.IsDismissed))]
        public bool IsDismissed { get; }

        public override void Validate()
        {
            base.Validate();
            if (AniListId is null != IsDismissed)
            {
                throw new DataValidationException(nameof(AniListId), "required");
            }
        }
    }
}
