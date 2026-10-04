using Jularr.Web.Features.Books;
using Jularr.Web.Features.Localization;
using Jularr.Web.Ui;

namespace Jularr.Tests;

[TestClass]
public sealed class LanguageEditionSelectorTests
{
    private static LanguageEditionSelectorModel Create(string current, IReadOnlyList<BookTranslationCoverage> coverage, bool translationEnabled, string source = "ja") =>
        BookLanguageEditionSelectorFactory.Create(
            "selector",
            "Work",
            source,
            current,
            10,
            false,
            coverage,
            translationEnabled,
            language => $"/Books/Library/1?lang={language}",
            "/Books/Library/1?handler=TranslateBook",
            UiTextBundle.English);

    [TestMethod]
    public void SourceLanguageIsTheOnlyOfficialEditionAndTranslationsAreGenerated()
    {
        var model = Create("ja", [new BookTranslationCoverage("de", 10)], translationEnabled: false);

        var original = model.Editions.Single(x => x.LanguageCode == "ja");
        var german = model.Editions.Single(x => x.LanguageCode == "de");

        Assert.AreEqual(EditionProvenance.Official, original.Provenance);
        Assert.AreEqual(EditionState.Current, original.State);
        Assert.AreEqual(EditionProvenance.Generated, german.Provenance);
        Assert.AreEqual(EditionState.Available, german.State);
        Assert.AreEqual(1, model.Editions.Count(x => x.Provenance == EditionProvenance.Official));
        Assert.AreEqual("Japanese", model.CurrentLanguage.Name);
    }

    [TestMethod]
    public void PartialAndMissingTranslationsExposeTranslateOnlyWhenAllowed()
    {
        var allowed = Create("de", [new BookTranslationCoverage("de", 4)], translationEnabled: true);
        var denied = Create("de", [new BookTranslationCoverage("de", 4)], translationEnabled: false);

        var partial = allowed.Editions.Single(x => x.LanguageCode == "de");
        var missing = allowed.Editions.Single(x => x.LanguageCode == "fr");

        Assert.AreEqual(EditionState.Current, partial.State);
        Assert.AreEqual("4 / 10 chapters translated", partial.Progress);
        Assert.IsNotNull(partial.Action);
        Assert.AreEqual(EditionState.Unavailable, missing.State);
        Assert.IsNull(missing.SelectUrl);
        Assert.AreEqual("fr", missing.Action!.Fields["lang"]);
        Assert.IsTrue(denied.Editions.All(x => x.Action is null));
    }

    [TestMethod]
    public void SelectorIsHiddenWhenNothingCanBeChosenOrRequested()
    {
        var onlyOriginal = Create("ja", [], translationEnabled: false);
        var withTranslation = Create("ja", [new BookTranslationCoverage("de", 10)], translationEnabled: false);
        var translatable = Create("ja", [], translationEnabled: true);

        Assert.IsFalse(onlyOriginal.IsMeaningful);
        Assert.IsTrue(withTranslation.IsMeaningful);
        Assert.IsTrue(translatable.IsMeaningful);
    }

    [TestMethod]
    public void LanguagesAreListedOnceEvenWhenCoverageDiffersInCase()
    {
        var model = Create("sv", [new BookTranslationCoverage("sv", 3), new BookTranslationCoverage("DE", 2)], translationEnabled: true);

        Assert.AreEqual(1, model.Languages.Count(x => x.Code == "sv"));
        Assert.AreEqual(1, model.Languages.Count(x => x.Code.Equals("de", StringComparison.OrdinalIgnoreCase)));
    }
}
