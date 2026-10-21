using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Reading;

namespace Jularr.Backend.Data.Logic.Reading
{
    internal sealed class ReadingPageNoteSaveData : BaseData
    {
        public ReadingPageNoteSaveData(long profileId, long mediaAssetId, int pageNumber, string? anchor, string note)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            PageNumber = pageNumber;
            Anchor = anchor;
            Note = note;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNumber))]
        public int PageNumber { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageAnchor), false)]
        public string? Anchor { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNote))]
        public string Note { get; }
    }

    internal sealed class ReadingPageNoteKeyData : BaseData
    {
        public ReadingPageNoteKeyData(long profileId, long mediaAssetId, long readingPageNoteId)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            ReadingPageNoteId = readingPageNoteId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNoteId))]
        public long ReadingPageNoteId { get; }
    }

    internal sealed class ReadingPageNoteIdData : BaseData
    {
        public ReadingPageNoteIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNoteId))]
        public long Id { get; }
    }
}
