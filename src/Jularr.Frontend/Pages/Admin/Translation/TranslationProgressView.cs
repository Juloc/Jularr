using System.Globalization;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin.Translation
{
    // How far the book translation is, as the Translation page and the job's drawer show it; the page keeps the full view current by reading it again.
    internal static class TranslationProgressView
    {
        // The works shown open before the rest folds away.
        private const int OpenWorks = 5;

        // The page's part: what happens now with the pace and the time left, the languages, then the books; the page swaps the whole part in while it is open.
        public static IUiComponent Full(BookTranslationProgressV1 progress, UiText texts)
        {
            var open = progress.Languages.Sum(language => (long)language.Total - language.Done);
            var sections = new List<IUiComponent>();
            var now = new List<IUiComponent>(Now(progress, texts));
            if (open > 0) now.Add(Facts(progress, open, texts));
            sections.Add(new UiSection("translation.progress.nowTitle", now));
            if (progress.Languages.Length > 0)
            {
                var languages = new List<IUiComponent>([.. progress.Languages.Select(language => Meter(Language(language.Language), language.Done, language.Total, texts))]);
                if (open > 0) languages.Add(new P(new Text("translation.progress.unitsHint")).Class("translation-progress__hint"));
                sections.Add(new UiSection("translation.progress.languages", languages));
            }

            var works = Works(progress.Books);
            if (works.Count > 0)
            {
                var books = new List<IUiComponent>([.. works.Take(OpenWorks).Select(work => Work(work, texts))]);
                if (works.Count > OpenWorks)
                    books.Add(new Details(new Summary(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["translation.progress.moreBooks"], (works.Count - OpenWorks).ToString("N0", CultureInfo.CurrentCulture)))),
                        new Div([.. works.Skip(OpenWorks).Select(work => Work(work, texts))])).Class("translation-progress__more"));
                sections.Add(new UiSection("translation.progress.books", books));
            }

            return new Div([.. sections]).Class("translation-progress").Attribute("data-translation-progress", "").Attribute("role", "status");
        }

        // The pace, the time left and the sections still open as three small figures.
        private static IUiComponent Facts(BookTranslationProgressV1 progress, long open, UiText texts) =>
            new Dl(
                Fact("translation.progress.pace", progress.BlocksPerMinute.ToString("N1", CultureInfo.CurrentCulture) + " " + texts["translation.progress.blocks"]),
                Fact("translation.progress.remaining", progress.SecondsRemaining is { } seconds ? Duration(seconds) : texts["translation.progress.unknown"]),
                Fact("translation.progress.openSections", open.ToString("N0", CultureInfo.CurrentCulture))).Class("translation-facts");

        private static IUiComponent Fact(string labelKey, string value) => new Div(new Dt(new Text(labelKey)), new Dd(new ValueText(value))).Class("translation-facts__fact");

        // Where in its book the job works now: the volume, the section and the language it translates into.
        public static string Where(BookTranslationCurrentV1 current, UiText texts) => (current.Volume is { } volume ? texts["translation.progress.volume"] + " " + volume + " · " : "")
            + texts["translation.progress.section"] + " " + current.Section.ToString(CultureInfo.CurrentCulture) + " → " + Language(current.Language);

        // What happens now first, then the pace and the time left, then the totals per language.
        public static IReadOnlyList<IUiComponent> Summary(BookTranslationProgressV1 progress, UiText texts)
        {
            var parts = new List<IUiComponent>(Now(progress, texts));
            var open = progress.Languages.Sum(language => (long)language.Total - language.Done);
            if (open > 0)
            {
                var left = progress.SecondsRemaining is { } seconds ? Duration(seconds) : texts["translation.progress.unknown"];
                parts.Add(new P(new ValueText(texts["translation.progress.pace"] + ": " + progress.BlocksPerMinute.ToString("N1", CultureInfo.CurrentCulture) + " "
                    + texts["translation.progress.blocks"] + " · " + texts["translation.progress.remaining"] + ": " + left)).Class("translation-progress__pace"));
            }

            parts.AddRange(progress.Languages.Select(language => Meter(Language(language.Language), language.Done, language.Total, texts)));
            return parts;
        }

        // What the job does now: the reason it cannot, the book and place it works on (with a warning when it stands still), or why it rests.
        private static IReadOnlyList<IUiComponent> Now(BookTranslationProgressV1 progress, UiText texts)
        {
            var parts = new List<IUiComponent>();
            var open = progress.Languages.Sum(language => (long)language.Total - language.Done);
            if (progress.IssueKey is { } issue) parts.Add(new P(new Text("translation.progress.issue." + issue)).Attribute("role", "alert"));
            else if (progress.Current is { } current)
            {
                parts.Add(new P(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["translation.progress.now"], current.Title + " · " + Where(current, texts)))).Class("translation-progress__now"));
                if (current.SecondsWithoutProgress >= 120)
                    parts.Add(new P(new ValueText(texts["translation.progress.stalled"] + " " + Duration(current.SecondsWithoutProgress))).Attribute("role", "alert"));
            }
            else parts.Add(new P(new Text(progress.Languages.Length == 0 ? "translation.progress.nothing" : open == 0 ? "translation.progress.done" : "translation.progress.waiting")));
            return parts;
        }

        // The books of one title (the volumes of a series) as one entry: the running ones first, then those still to start, the finished last.
        private sealed record WorkGroup(string Title, IReadOnlyList<BookTranslationBookV1> Volumes, int Done, int Total)
        {
            public bool Finished => Total > 0 && Done >= Total;
            public bool Running => Done > 0 && Done < Total;
            public int FinishedVolumes => Volumes.Count(volume => volume.Total > 0 && volume.Done >= volume.Total);
        }

        private static List<WorkGroup> Works(BookTranslationBookV1[] books) =>
            [.. books.GroupBy(book => book.WorkId)
                .Select(group => new WorkGroup(group.First().Title,
                    [.. group.OrderBy(book => decimal.TryParse(book.Volume, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : 0m)],
                    group.Sum(book => book.Done), group.Sum(book => book.Total)))
                .OrderBy(work => work.Finished ? 2 : work.Running ? 0 : 1)
                .ThenByDescending(work => work.Total == 0 ? 0 : (double)work.Done / work.Total)
                .ThenBy(work => work.Title, StringComparer.CurrentCultureIgnoreCase)];

        private static IUiComponent Work(WorkGroup work, UiText texts)
        {
            if (work.Volumes.Count == 1)
            {
                var only = work.Volumes[0];
                return Meter(only.Volume is { } volume ? only.Title + " · " + texts["translation.progress.volume"] + " " + volume : only.Title, only.Done, only.Total, texts);
            }

            var header = string.Format(CultureInfo.CurrentCulture, texts["translation.progress.volumesDone"], work.FinishedVolumes.ToString("N0", CultureInfo.CurrentCulture), work.Volumes.Count.ToString("N0", CultureInfo.CurrentCulture));
            return new Details(
                new Summary(Meter(work.Title + " · " + header, work.Done, work.Total, texts)),
                new Div([.. work.Volumes.Select(book => Meter(book.Volume is { } volume ? texts["translation.progress.volume"] + " " + volume : book.Title, book.Done, book.Total, texts))]))
                .Class("translation-progress__work");
        }

        private static string Language(string code)
        {
            try { return CultureInfo.GetCultureInfo(code).DisplayName; }
            catch (CultureNotFoundException) { return code; }
        }

        private static IUiComponent Meter(string label, int done, int total, UiText texts)
        {
            var percent = total <= 0 ? 0 : (int)Math.Floor(done * 100.0 / total);
            return new Div(
                new Div(new Span(new ValueText(label)).Class("translation-progress__label"),
                    new Span(new ValueText(percent.ToString(CultureInfo.CurrentCulture) + " % · " + done.ToString("N0", CultureInfo.CurrentCulture) + " " + texts["translation.progress.of"] + " "
                        + total.ToString("N0", CultureInfo.CurrentCulture) + " " + texts["translation.progress.sections"])).Class("translation-progress__figures")).Class("translation-progress__head"),
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
