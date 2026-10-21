using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Reading;

namespace Jularr.Backend.Data.Logic.Reading
{
    internal sealed class ReadingBookmarkSaveData : BaseData
    {
        public ReadingBookmarkSaveData(long profileId, long mediaAssetId, int sectionIndex, int block, decimal progression, string? title)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            SectionIndex = sectionIndex;
            Block = block;
            Progression = progression;
            Title = title;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(ReadingBookmarkFields), nameof(ReadingBookmarkFields.Block))]
        public int Block { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Progression))]
        public decimal Progression { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Title))]
        public string? Title { get; }
    }

    internal sealed class ReadingBookmarkKeyData : BaseData
    {
        public ReadingBookmarkKeyData(long profileId, long mediaAssetId, long readingBookmarkId)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            ReadingBookmarkId = readingBookmarkId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingBookmarkFields), nameof(ReadingBookmarkFields.Id))]
        public long ReadingBookmarkId { get; }
    }

    internal sealed class ReadingBookmarkIdData : BaseData
    {
        public ReadingBookmarkIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(ReadingBookmarkFields), nameof(ReadingBookmarkFields.Id))]
        public long Id { get; }
    }
}
