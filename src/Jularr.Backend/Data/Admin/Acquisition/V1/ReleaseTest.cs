using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Admin.Wanted.V1;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    // A release name to try against every video profile.
    public sealed class TestReleaseParametersV1 : BaseParameters
    {
        public TestReleaseParametersV1(string title)
        {
            Title = title;
        }

        [DataField(typeof(ReleaseTestFields), nameof(ReleaseTestFields.Title))]
        public string Title { get; }
    }

    // What the name says (quality and group) and what every video profile makes of it: its points and whether it would take it, else why not.
    public sealed class ReleaseTestV1 : BaseResponse
    {
        public ReleaseTestV1(string quality, string? releaseGroup, BaseDataList<ReleaseTestProfileV1> profiles)
        {
            Quality = quality;
            ReleaseGroup = releaseGroup;
            Profiles = profiles;
        }

        [DataField(typeof(ReleaseTestFields), nameof(ReleaseTestFields.Quality))]
        public string Quality { get; }

        [DataField(typeof(ReleaseTestFields), nameof(ReleaseTestFields.Quality), false)]
        public string? ReleaseGroup { get; }

        public BaseDataList<ReleaseTestProfileV1> Profiles { get; }
    }

    public sealed class ReleaseTestProfileV1 : BaseData
    {
        public ReleaseTestProfileV1(long id, string name, bool accepted, int score, string? rejection, BaseDataList<ReleaseScorePointV1> points)
        {
            Id = id;
            Name = name;
            Accepted = accepted;
            Score = score;
            Rejection = rejection;
            Points = points;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.IsInUse))]
        public bool Accepted { get; }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.Score))]
        public int Score { get; }

        // The first reason the profile would not take it.
        [DataField(typeof(ReleaseTestFields), nameof(ReleaseTestFields.Quality), false)]
        public string? Rejection { get; }

        public BaseDataList<ReleaseScorePointV1> Points { get; }
    }
}
