using Jularr.Web.Features.Localization;
using Jularr.Web.Ui;

namespace Jularr.Web.Features.Books;

/// <summary>
/// Maps a book's source language and cached chapter translations onto the shared language/edition selector. A book has exactly
/// one official edition (its source language) and at most one generated translation per target language; no Edition backend
/// model exists, so the translation cache is the edition inventory.
/// </summary>
public static class BookLanguageEditionSelectorFactory
{
    public static LanguageEditionSelectorModel Create(
        string id,
        string workTitle,
        string sourceLanguage,
        string currentLanguage,
        int totalChapters,
        bool isPdf,
        IReadOnlyList<BookTranslationCoverage> coverage,
        bool translationEnabled,
        Func<string, string> selectUrl,
        string translateUrl,
        UiTextBundle ui)
    {
        var languageCodes = new[] { currentLanguage, sourceLanguage }
            .Concat(coverage.Select(x => x.Language))
            .Concat(BookLanguageCatalog.Supported.Select(x => x.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var languages = languageCodes.Select(code => new LanguageOption(code, BookLanguageCatalog.GetName(code))).ToList();
        var editions = new List<EditionOption>();

        foreach (var language in languages)
        {
            var isCurrent = language.Code.Equals(currentLanguage, StringComparison.OrdinalIgnoreCase);

            if (language.Code.Equals(sourceLanguage, StringComparison.OrdinalIgnoreCase))
            {
                editions.Add(new EditionOption(
                    $"original-{language.Code}",
                    language.Code,
                    ui.Format("languageEdition.originalTitle", ("language", language.Name)),
                    ui["languageEdition.originalDescription"],
                    EditionProvenance.Official,
                    isCurrent ? EditionState.Current : EditionState.Available,
                    selectUrl(language.Code),
                    null,
                    null));
                continue;
            }

            var translated = coverage.FirstOrDefault(x => x.Language.Equals(language.Code, StringComparison.OrdinalIgnoreCase))?.TranslatedChapters ?? 0;
            var isComplete = totalChapters > 0 && translated >= totalChapters;
            var state = translated == 0 ? EditionState.Unavailable : isComplete ? EditionState.Available : EditionState.Partial;
            if (isCurrent && translated > 0)
            {
                state = EditionState.Current;
            }

            var action = !isComplete && translationEnabled
                ? new EditionAction(ui["languageEdition.translate"], translateUrl, new Dictionary<string, string> { ["lang"] = language.Code })
                : null;
            var progress = translated > 0 && !isComplete
                ? ui.Format(isPdf ? "books.library.pagesTranslated" : "books.library.chaptersTranslated", ("translated", translated), ("total", totalChapters))
                : null;

            editions.Add(new EditionOption(
                $"generated-{language.Code}",
                language.Code,
                ui.Format("languageEdition.generatedTitle", ("language", language.Name)),
                ui["languageEdition.generatedDescription"],
                EditionProvenance.Generated,
                state,
                translated > 0 ? selectUrl(language.Code) : null,
                action,
                progress));
        }

        return new LanguageEditionSelectorModel(id, workTitle, currentLanguage, languages, editions, ui);
    }
}
