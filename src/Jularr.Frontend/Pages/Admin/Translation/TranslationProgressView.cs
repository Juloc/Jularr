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

        public static IUiComponent Full(BookTranslationProgressV1 progress, UiText texts)
        {
            var parts = new List<IUiComponent>(Summary(progress, texts));
            var works = Works(progress.Books);
            if (works.Count > 0)
            {
                parts.Add(new H3(new Text("translation.progress.books")));
                parts.AddRange(works.Take(OpenWorks).Select(work => Work(work, texts)));
                if (works.Count > OpenWorks)
                    parts.Add(new Details(new Summary(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["translation.progress.moreBooks"], works.Count - OpenWorks))),
                        new Div([.. works.Skip(OpenWorks).Select(work => Work(work, texts))])).Class("translation-progress__more"));
            }

            return new Div([.. parts]).Class("feature-card").Attribute("data-translation-progress", "").Attribute("role", "status");
        }

        // What happens now first, then the pace and the time left, then the totals per language.
        public static IReadOnlyList<IUiComponent> Summary(BookTranslationProgressV1 progress, UiText texts)
        {
            var parts = new List<IUiComponent>();
            var open = progress.Languages.Sum(language => (long)language.Total - language.Done);
            if (progress.IssueKey is { } issue) parts.Add(new P(new Text("translation.progress.issue." + issue)).Attribute("role", "alert"));
            else if (progress.Current is { } current)
            {
                var where = current.Title + (current.Volume is { } volume ? " · " + texts["translation.progress.volume"] + " " + volume : "") + " · "
                    + texts["translation.progress.section"] + " " + current.Section.ToString(CultureInfo.CurrentCulture) + " → " + Language(current.Language);
                parts.Add(new P(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["translation.progress.now"], where))).Class("translation-progress__now"));
                if (current.SecondsWithoutProgress >= 120)
                    parts.Add(new P(new ValueText(texts["translation.progress.stalled"] + " " + Duration(current.SecondsWithoutProgress))).Attribute("role", "alert"));
            }
            else parts.Add(new P(new Text(progress.Languages.Length == 0 ? "translation.progress.nothing" : open == 0 ? "translation.progress.done" : "translation.progress.waiting")));

            if (open > 0)
            {
                var left = progress.SecondsRemaining is { } seconds ? Duration(seconds) : texts["translation.progress.unknown"];
                parts.Add(new P(new ValueText(texts["translation.progress.pace"] + ": " + progress.BlocksPerMinute.ToString("0.0", CultureInfo.CurrentCulture) + " "
                    + texts["translation.progress.blocks"] + " · " + texts["translation.progress.remaining"] + ": " + left)).Class("translation-progress__pace"));
            }

            parts.AddRange(progress.Languages.Select(language => Meter(Language(language.Language), language.Done, language.Total, texts)));
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

            var header = string.Format(CultureInfo.CurrentCulture, texts["translation.progress.volumesDone"], work.FinishedVolumes, work.Volumes.Count);
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
                new P(new ValueText(label + ": " + percent.ToString(CultureInfo.CurrentCulture) + " % · " + done.ToString(CultureInfo.CurrentCulture) + " " + texts["translation.progress.of"] + " "
                    + total.ToString(CultureInfo.CurrentCulture) + " " + texts["translation.progress.sections"])),
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
