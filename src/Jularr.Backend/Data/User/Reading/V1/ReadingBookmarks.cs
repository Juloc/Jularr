using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Reading;

namespace Jularr.Backend.Data.User.Reading.V1
{
    public sealed class ReadingBookmarkParametersV1 : BaseParameters
    {
        public ReadingBookmarkParametersV1(long mediaAssetId, long readingBookmarkId)
        {
            MediaAssetId = mediaAssetId;
            ReadingBookmarkId = readingBookmarkId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingBookmarkFields), nameof(ReadingBookmarkFields.Id))]
        public long ReadingBookmarkId { get; }
    }

    // A place in a book: prose by section and block, images by page (the section index of an image book).
    public sealed class ReadingBookmarkV1 : BaseResponse
    {
        public ReadingBookmarkV1(long id, int sectionIndex, int block, decimal progression, string? title)
        {
            Id = id;
            SectionIndex = sectionIndex;
            Block = block;
            Progression = progression;
            Title = title;
        }

        [DataField(typeof(ReadingBookmarkFields), nameof(ReadingBookmarkFields.Id))]
        public long Id { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(ReadingBookmarkFields), nameof(ReadingBookmarkFields.Block))]
        public int Block { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Progression))]
        public decimal Progression { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Title))]
        public string? Title { get; }
    }

    public sealed class CreateReadingBookmarkDataV1 : BaseData
    {
        public CreateReadingBookmarkDataV1(int sectionIndex, int block, decimal progression, string? title)
        {
            SectionIndex = sectionIndex;
            Block = block;
            Progression = progression;
            Title = title;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(ReadingBookmarkFields), nameof(ReadingBookmarkFields.Block))]
        public int Block { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Progression))]
        public decimal Progression { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Title))]
        public string? Title { get; }
    }

    public sealed class CreateReadingBookmarkResultV1 : BaseResponse
    {
        public CreateReadingBookmarkResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(ReadingBookmarkFields), nameof(ReadingBookmarkFields.Id))]
        public long Id { get; }
    }
}
