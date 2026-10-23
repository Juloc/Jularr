namespace Jularr.Backend.Service.Core.Localization
{
    // A language Jularr ships: reviewed ones are complete and checked, others machine-translated until someone reviews them.
    // A text counts only while the English it was made from is still the source.
    public sealed record UiTextCatalog(string Locale, string Name, bool IsRightToLeft, bool IsReviewed, bool IsEnabled,
        IReadOnlyDictionary<string, (string English, string Text)> Texts);
}
