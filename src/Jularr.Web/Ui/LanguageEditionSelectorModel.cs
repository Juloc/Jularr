using Jularr.Web.Features.Localization;

namespace Jularr.Web.Ui;

public enum EditionProvenance
{
    Official,
    Generated
}

public enum EditionState
{
    Current,
    Available,
    Partial,
    Unavailable
}

/// <summary>A secondary action of an edition row, posted as a regular antiforgery-protected form (for example "Translate").</summary>
public sealed record EditionAction(string Label, string PostUrl, IReadOnlyDictionary<string, string> Fields);

public sealed record LanguageOption(string Code, string Name);

/// <summary>
/// One selectable presentation of a work. <paramref name="SelectUrl"/> is where choosing the row navigates; it is null while the
/// edition cannot be opened. <paramref name="Progress"/> is a ready-made completeness label shown for partial editions.
/// </summary>
public sealed record EditionOption(
    string Key,
    string LanguageCode,
    string Title,
    string Description,
    EditionProvenance Provenance,
    EditionState State,
    string? SelectUrl,
    EditionAction? Action,
    string? Progress);

/// <summary>
/// View model of the shared <c>_LanguageEditionSelector</c> partial (docs/mockups/language-edition-selector/SPEC.md).
/// Hosts build it with their own edition data and render <c>&lt;partial name="_LanguageEditionSelector" model="..." /&gt;</c>;
/// the partial pulls in its own CSS/JS. <paramref name="Id"/> must be unique per page. The partial never mutates anything:
/// selecting a row navigates to <see cref="EditionOption.SelectUrl"/> after dispatching a cancelable
/// <c>jularr:edition-selected</c> event on the dialog, so an in-page consumer (the reader) can switch without navigation.
/// </summary>
public sealed record LanguageEditionSelectorModel(
    string Id,
    string WorkTitle,
    string CurrentLanguageCode,
    IReadOnlyList<LanguageOption> Languages,
    IReadOnlyList<EditionOption> Editions,
    UiTextBundle Ui)
{
    public LanguageOption CurrentLanguage => Languages.First(x => x.Code.Equals(CurrentLanguageCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>The spec shows the selector only when something meaningful can be chosen or requested.</summary>
    public bool IsMeaningful => Editions.Count(x => x.State != EditionState.Unavailable) > 1 || Editions.Any(x => x.Action is not null);
}
