using System.Globalization;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin.Translation
{
    // How far the book translation is, as the Translation page and the job's drawer show it; the page keeps the full view current by reading it again.
    internal static class TranslationProgressView
    {
        public static IUiComponent Full(BookTranslationProgressV1 progress, UiText texts)
        {
            var parts = new List<IUiComponent> { new H2(new Text("translation.progress.title")) };
            parts.AddRange(Summary(progress, texts));
            if (progress.Books.Length > 0)
            {
                parts.Add(new H3(new Text("translation.progress.books")));
                parts.AddRange(progress.Books.Select(book => Meter(book.Volume is { } volume ? book.Title + " · " + texts["translation.progress.volume"] + " " + volume : book.Title, book.Done, book.Total, texts)));
            }

            return new Div([.. parts]).Class("feature-card").Attribute("data-translation-progress", "").Attribute("role", "status");
        }

        // The totals per language, the section in work, why nothing moves, the pace and the time left.
        public static IReadOnlyList<IUiComponent> Summary(BookTranslationProgressV1 progress, UiText texts)
        {
            var parts = new List<IUiComponent>();
            if (progress.Languages.Length == 0) parts.Add(new P(new Text("translation.progress.nothing")));
            parts.AddRange(progress.Languages.Select(language => Meter(language.Language, language.Done, language.Total, texts)));
            if (progress.Current is { } current)
            {
                parts.Add(new P(new ValueText(texts["translation.progress.current"] + ": " + current.Title + (current.Volume is { } volume ? " · " + texts["translation.progress.volume"] + " " + volume : "") + " · " + texts["translation.progress.chapter"] + " "
                    + current.Section.ToString(CultureInfo.CurrentCulture) + " → " + current.Language)));
                if (current.SecondsWithoutProgress >= 120)
                    parts.Add(new P(new ValueText(texts["translation.progress.stalled"] + " " + Duration(current.SecondsWithoutProgress))).Attribute("role", "alert"));
            }

            if (progress.IssueKey is { } issue) parts.Add(new P(new Text("translation.progress.issue." + issue)).Attribute("role", "alert"));
            var left = progress.SecondsRemaining is { } seconds ? Duration(seconds) : texts["translation.progress.unknown"];
            parts.Add(new P(new ValueText(texts["translation.progress.pace"] + ": " + progress.BlocksPerMinute.ToString("0.0", CultureInfo.CurrentCulture) + " "
                + texts["translation.progress.blocks"] + " · " + texts["translation.progress.remaining"] + ": " + left)));
            return parts;
        }

        private static IUiComponent Meter(string label, int done, int total, UiText texts)
        {
            var percent = total <= 0 ? 0 : (int)Math.Floor(done * 100.0 / total);
            return new Div(
                new P(new ValueText(label + " · " + done.ToString(CultureInfo.CurrentCulture) + " " + texts["translation.progress.of"] + " " + total.ToString(CultureInfo.CurrentCulture)
                    + " " + texts["translation.progress.chapters"] + " (" + percent.ToString(CultureInfo.CurrentCulture) + " %)")),
                new Progress().Attribute("max", "100").Attribute("value", percent.ToString(CultureInfo.InvariantCulture)).TextAttribute("aria-label", "translation.progress.title"))
                .Class("translation-progress__row");
        }

        private static string Duration(long seconds) => seconds switch
        {
            >= 86_400 => (seconds / 86_400).ToString(CultureInfo.CurrentCulture) + " d " + (seconds % 86_400 / 3_600).ToString(CultureInfo.CurrentCulture) + " h",
            >= 3_600 => (seconds / 3_600).ToString(CultureInfo.CurrentCulture) + " h " + (seconds % 3_600 / 60).ToString(CultureInfo.CurrentCulture) + " min",
            >= 60 => (seconds / 60).ToString(CultureInfo.CurrentCulture) + " min",
            _ => seconds.ToString(CultureInfo.CurrentCulture) + " s"
        };
    }
}
