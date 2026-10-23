using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Reading;

namespace Jularr.Backend.Data.Logic.Reading
{
    internal sealed class ReaderSettingsSaveData : BaseData
    {
        public ReaderSettingsSaveData(long profileId, ReaderFont fontTypeId, int size, int lineSpacing, int paragraphSpacing, int textWidth, bool hyphenation)
        {
            ProfileId = profileId;
            FontTypeId = fontTypeId;
            Size = size;
            LineSpacing = lineSpacing;
            ParagraphSpacing = paragraphSpacing;
            TextWidth = textWidth;
            Hyphenation = hyphenation;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

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
}
