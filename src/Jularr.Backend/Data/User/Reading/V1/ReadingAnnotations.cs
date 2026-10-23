using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Reading;

namespace Jularr.Backend.Data.User.Reading.V1
{
    public sealed class ReadingAnnotationsParametersV1 : BaseParameters
    {
        public ReadingAnnotationsParametersV1(long mediaAssetId, int page = 1, int pageSize = 50)
        {
            MediaAssetId = mediaAssetId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class ReadingSectionAnnotationsParametersV1 : BaseParameters
    {
        public ReadingSectionAnnotationsParametersV1(long mediaAssetId, int sectionIndex)
        {
            MediaAssetId = mediaAssetId;
            SectionIndex = sectionIndex;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }
    }

    public sealed class ReadingAnnotationParametersV1 : BaseParameters
    {
        public ReadingAnnotationParametersV1(long mediaAssetId, long readingAnnotationId)
        {
            MediaAssetId = mediaAssetId;
            ReadingAnnotationId = readingAnnotationId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Id))]
        public long ReadingAnnotationId { get; }
    }

    // From the start block and offset up to, not including, the end offset of the end block.
    public sealed class ReadingAnnotationV1 : BaseResponse
    {
        public ReadingAnnotationV1(long id, int sectionIndex, int startBlock, int startOffset, int endBlock, int endOffset, string quote, string? note, HighlightColorType highlightColorTypeId)
        {
            Id = id;
            SectionIndex = sectionIndex;
            StartBlock = startBlock;
            StartOffset = startOffset;
            EndBlock = endBlock;
            EndOffset = endOffset;
            Quote = quote;
            Note = note;
            HighlightColorTypeId = highlightColorTypeId;
        }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Id))]
        public long Id { get; }

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

    public sealed class CreateReadingAnnotationDataV1 : BaseData
    {
        public CreateReadingAnnotationDataV1(int sectionIndex, int startBlock, int startOffset, int endBlock, int endOffset, string quote, string? note, HighlightColorType highlightColorTypeId)
        {
            SectionIndex = sectionIndex;
            StartBlock = startBlock;
            StartOffset = startOffset;
            EndBlock = endBlock;
            EndOffset = endOffset;
            Quote = quote;
            Note = note;
            HighlightColorTypeId = highlightColorTypeId;
        }

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

    public sealed class UpdateReadingAnnotationDataV1 : BaseData
    {
        public UpdateReadingAnnotationDataV1(string? note, HighlightColorType highlightColorTypeId)
        {
            Note = note;
            HighlightColorTypeId = highlightColorTypeId;
        }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Note))]
        public string? Note { get; }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.HighlightColorTypeId))]
        public HighlightColorType HighlightColorTypeId { get; }
    }

    public sealed class CreateReadingAnnotationResultV1 : BaseResponse
    {
        public CreateReadingAnnotationResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(ReadingAnnotationFields), nameof(ReadingAnnotationFields.Id))]
        public long Id { get; }
    }
}
