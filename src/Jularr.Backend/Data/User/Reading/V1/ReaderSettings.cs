using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Fields.Reading;

namespace Jularr.Backend.Data.User.Reading.V1
{
    public sealed class ReaderSettingsParametersV1 : BaseParameters
    {
    }

    // How the profile's reading text looks on all its devices; without a row these are the defaults.
    public sealed class ReaderSettingsV1 : BaseResponse
    {
        public ReaderSettingsV1(ReaderFont fontTypeId, int size, int lineSpacing, int paragraphSpacing, int textWidth, bool hyphenation, bool isSaved)
        {
            FontTypeId = fontTypeId;
            Size = size;
            LineSpacing = lineSpacing;
            ParagraphSpacing = paragraphSpacing;
            TextWidth = textWidth;
            Hyphenation = hyphenation;
            IsSaved = isSaved;
        }

        // False while the profile has chosen nothing yet; its devices then keep what they remembered.
        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.IsSaved))]
        public bool IsSaved { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.FontTypeId))]
        public ReaderFont FontTypeId { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.Size))]
        public int Size { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.LineSpacing))]
        public int LineSpacing { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.ParagraphSpacing))]
        public int ParagraphSpacing { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.TextWidth))]
        public int TextWidth { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.Hyphenation))]
        public bool Hyphenation { get; }
    }

    public sealed class SaveReaderSettingsDataV1 : BaseData
    {
        public SaveReaderSettingsDataV1(ReaderFont fontTypeId, int size, int lineSpacing, int paragraphSpacing, int textWidth, bool hyphenation)
        {
            FontTypeId = fontTypeId;
            Size = size;
            LineSpacing = lineSpacing;
            ParagraphSpacing = paragraphSpacing;
            TextWidth = textWidth;
            Hyphenation = hyphenation;
        }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.FontTypeId))]
        public ReaderFont FontTypeId { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.Size))]
        public int Size { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.LineSpacing))]
        public int LineSpacing { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.ParagraphSpacing))]
        public int ParagraphSpacing { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.TextWidth))]
        public int TextWidth { get; }

        [DataField(typeof(ReaderSettingFields), nameof(ReaderSettingFields.Hyphenation))]
        public bool Hyphenation { get; }
    }

    // Where each section of a book starts, as characters before it; the reader's progress slider maps a position in the whole book onto a section with it.
    public sealed class ReadingSectionOffsetV1 : BaseResponse
    {
        public ReadingSectionOffsetV1(int sectionIndex, long characters, long charactersBefore)
        {
            SectionIndex = sectionIndex;
            Characters = characters;
            CharactersBefore = charactersBefore;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.TotalCharacters))]
        public long Characters { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.TotalCharacters))]
        public long CharactersBefore { get; }
    }
}
