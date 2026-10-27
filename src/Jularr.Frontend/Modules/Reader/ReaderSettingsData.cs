using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields;

namespace Jularr.Frontend.Modules.Reader
{
    // Presentation-only device preferences; never a second reading-progress store.
    public sealed class ReaderSettingsData : BaseData
    {
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Choice))]
        public string Preset { get; } = "book";
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Choice))]
        public string Mode { get; } = "paged";
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Choice))]
        public string Layout { get; } = "auto";
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Choice))]
        public string Direction { get; } = "ltr";
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Choice))]
        public string Surface { get; } = "paper";
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Colour))]
        public string Colour { get; } = "#f7f3ec";
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Choice))]
        public string Animation { get; } = "system";
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Size))]
        public int Size { get; } = 20;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Spacing))]
        public int Spacing { get; } = 165;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Choice))]
        public string FontFamily { get; } = "serif";
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Paragraph))]
        public int Paragraph { get; } = 100;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.TextWidth))]
        public int TextWidth { get; } = 68;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Margin))]
        public int MarginTop { get; } = 6;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Margin))]
        public int MarginBottom { get; } = 6;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Margin))]
        public int MarginInner { get; } = 8;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Margin))]
        public int MarginOuter { get; } = 7;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Enabled))]
        public bool Indent { get; } = false;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Enabled))]
        public bool Hyphenation { get; } = true;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Enabled))]
        public bool Illustrations { get; } = true;
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Choice))]
        public string Fit { get; } = "page";
    }

    // The sleep timer of read-aloud: minutes until it stops, 0 for none. Transient, never saved.
    public sealed class ReaderTimerData : BaseData
    {
        [DataField(typeof(ReaderSettingsFields), nameof(ReaderSettingsFields.Choice))]
        public string Minutes { get; } = "0";
    }

    public static class ReaderSettingsFields
    {
        public static readonly StringField Choice = new() { MaximumLength = 16 };
        public static readonly StringField Colour = new() { MinimumLength = 7, MaximumLength = 7 };
        public static readonly IntField Size = new() { Minimum = 14, Maximum = 40 };
        public static readonly IntField Spacing = new() { Minimum = 120, Maximum = 220 };
        public static readonly IntField Paragraph = new() { Minimum = 0, Maximum = 200 };
        public static readonly IntField TextWidth = new() { Minimum = 40, Maximum = 90 };
        public static readonly IntField Margin = new() { Minimum = 0, Maximum = 20 };
        public static readonly BoolField Enabled = new();
    }
}
