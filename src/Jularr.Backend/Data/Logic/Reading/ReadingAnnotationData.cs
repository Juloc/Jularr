using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Reading;

namespace Jularr.Backend.Data.Logic.Reading
{
    internal sealed class ReadingAnnotationSaveData : BaseData
    {
        public ReadingAnnotationSaveData(long profileId, long mediaAssetId, int sectionIndex, int startBlock, int startOffset, int endBlock, int endOffset, string quote, string? note,
            HighlightColorType highlightColorTypeId)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            SectionIndex = sectionIndex;
            StartBlock = startBlock;
            StartOffset = startOffset;
            EndBlock = endBlock;
            EndOffset = endOffset;
            Quote = quote;
            Note = note;
            HighlightColorTypeId = highlightColorTypeId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Position))]
        public int StartBlock { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Position))]
        public int StartOffset { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Position))]
        public int EndBlock { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Position))]
        public int EndOffset { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Quote))]
        public string Quote { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Note))]
        public string? Note { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.HighlightColorTypeId))]
        public HighlightColorType HighlightColorTypeId { get; }
    }

    internal sealed class ReadingAnnotationUpdateData : BaseData
    {
        public ReadingAnnotationUpdateData(long profileId, long mediaAssetId, long readingAnnotationId, string? note, HighlightColorType highlightColorTypeId)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            ReadingAnnotationId = readingAnnotationId;
            Note = note;
            HighlightColorTypeId = highlightColorTypeId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Id))]
        public long ReadingAnnotationId { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Note))]
        public string? Note { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.HighlightColorTypeId))]
        public HighlightColorType HighlightColorTypeId { get; }
    }

    internal sealed class ReadingAnnotationKeyData : BaseData
    {
        public ReadingAnnotationKeyData(long profileId, long mediaAssetId, long readingAnnotationId)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            ReadingAnnotationId = readingAnnotationId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Id))]
        public long ReadingAnnotationId { get; }
    }

    internal sealed class ReadingAnnotationIdData : BaseData
    {
        public ReadingAnnotationIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Id))]
        public long Id { get; }
    }
}
