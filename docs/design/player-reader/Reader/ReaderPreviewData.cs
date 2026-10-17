using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields;

namespace UiDesigner.Projects.Jularr.Reader
{
    // Local design values, never persisted.
    public sealed class ReaderPreviewData : BaseData
    {
        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.Preset))]
        public string Preset { get; } = "book";

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.FontSize))]
        public int FontSize { get; } = 20;

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.LineHeight))]
        public int LineHeight { get; } = 165;

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.TextWidth))]
        public int TextWidth { get; } = 68;

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.FontFamily))]
        public string FontFamily { get; } = "serif";

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.Layout))]
        public string Layout { get; } = "auto";

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.ReadingMode))]
        public string ReadingMode { get; } = "paged";

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.ContentMode))]
        public string ContentMode { get; } = "text";

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.Direction))]
        public string Direction { get; } = "ltr";

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.ReaderTheme))]
        public string ReaderTheme { get; } = "paper";

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.Illustrations))]
        public bool Illustrations { get; } = false;

        [DataField(typeof(ReaderPreviewFields), nameof(ReaderPreviewFields.SurfaceColor))]
        public string SurfaceColor { get; } = "#f7f3ec";
    }

    public static class ReaderPreviewFields
    {
        public static readonly StringField Preset = new() { MaximumLength = 20 };
        public static readonly IntField FontSize = new() { Minimum = 14, Maximum = 32 };
        public static readonly IntField LineHeight = new() { Minimum = 130, Maximum = 200 };
        public static readonly IntField TextWidth = new() { Minimum = 40, Maximum = 90 };
        public static readonly StringField FontFamily = new() { MaximumLength = 32 };
        public static readonly StringField Layout = new() { MaximumLength = 16 };
        public static readonly StringField ReadingMode = new() { MaximumLength = 16 };
        public static readonly StringField ContentMode = new() { MaximumLength = 16 };
        public static readonly StringField Direction = new() { MaximumLength = 8 };
        public static readonly StringField ReaderTheme = new() { MaximumLength = 16 };
        public static readonly BoolField Illustrations = new();
        public static readonly StringField SurfaceColor = new() { MinimumLength = 7, MaximumLength = 7 };
    }
}
