using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Jularr.Web.Features.Providers;

public enum ProviderFamily
{
    Metadata
}

public enum ProviderConnectionState
{
    NotConfigured,
    Disabled,

    /// <summary>A credential exists but no call has been observed since it was set.</summary>
    Unknown,
    Healthy,
    Degraded,
    AuthenticationFailed,
    Unavailable
}

/// <summary>What a provider form tells the admin after a post. Never carries a value that was typed.</summary>
public enum ProviderFeedback
{
    None,
    Saved,

    /// <summary>Saved, and the one connection test that follows a save of a usable credential succeeded.</summary>
    SavedConnectionWorks,
    Removed,
    InvalidInput,
    TestSucceeded,

    /// <summary>A credential that was typed (and not saved) works: the answer says so and that it still needs Save.</summary>
    TestSucceededUnsaved,
    TestAuthenticationFailed,
    TestUnreachable,
    TestRateLimited,
    TestNotConfigured
}

/// <summary>A masked, write-only field of a provider: it only says whether a value is saved and never carries it.</summary>
public sealed record ProviderSecretField(string Name, string LabelKey, string HintKey, int MaxLength, bool HasSavedValue);

/// <summary>Why Setup cannot finish while the provider is unusable, in the keys of the text shown and the action that turns the dependent features off.</summary>
public sealed record ProviderBlocking(string TitleKey, string BodyKey, string ConfigureActionKey, string DisableActionKey);

/// <summary>
/// Everything the shared Provider UI (Admin → Providers and the Setup provider step) needs to render one provider, as data: no provider-native type
/// reaches the markup, and no secret, not even a fragment of one, is part of it.
/// </summary>
/// <param name="RequiredReasonKey">Set while an enabled feature needs this provider: the localization key of the reason, which also marks it required.</param>
/// <param name="CredentialSourceKey">Where the effective credential comes from, as a localization key.</param>
/// <param name="Blocking">Set only while the provider is required and unusable, so Setup must not finish.</param>
public sealed record ProviderView(
    string Key,
    string DisplayName,
    string SummaryKey,
    ProviderFamily Family,
    bool Enabled,
    ProviderConnectionState State,
    string? RequiredReasonKey,
    IReadOnlyList<string> CapabilityKeys,
    IReadOnlyList<ProviderSecretField> Fields,
    bool ExternallyManaged,
    bool HasSavedValue,
    bool SavedValueUnreadable,
    string CredentialSourceKey,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    ProviderBlocking? Blocking)
{
    public string Initials => DisplayName.Length <= 4 ? DisplayName : DisplayName[..4];
}

/// <summary>
/// The contract a provider fulfils to appear in the shared Provider UI: its view, and the save, test and remove operations behind the form.
/// A provider is described by its <see cref="ProviderView"/> schema (masked fields, capabilities, state); the UI never has a provider-specific page.
/// </summary>
public interface IProviderSettings
{
    string Key { get; }

    Task<ProviderView> GetViewAsync(CancellationToken cancellationToken);

    /// <summary>Saves the enabled switch and every secret field that is given by name; a field that is empty keeps its saved value.</summary>
    Task<ProviderFeedback> SaveAsync(bool enabled, IReadOnlyDictionary<string, string?> secrets, CancellationToken cancellationToken);

    /// <summary>Asks the provider once: with the given unsaved values, or with the saved configuration when none is given.</summary>
    Task<ProviderFeedback> TestAsync(IReadOnlyDictionary<string, string?> secrets, CancellationToken cancellationToken);

    Task RemoveSavedValuesAsync(CancellationToken cancellationToken);

    /// <summary>Turns off the instance features that need this provider, for an instance that does not want to configure it.</summary>
    Task DisableDependentFeaturesAsync(CancellationToken cancellationToken);
}

/// <summary>The model of a shared provider partial: the viewer's text, the providers and the page that posts return to.</summary>
/// <param name="SelectedKey">The provider whose form is open.</param>
/// <param name="ReturnUrl">The local page a post returns to, so one set of handlers serves Admin and Setup.</param>
/// <param name="FeedbackProviderKey">The provider the feedback belongs to.</param>
public sealed record ProviderPageModel(UiTextBundle Ui, IReadOnlyList<ProviderView> Providers, string? SelectedKey, string ReturnUrl, ProviderFeedback Feedback, string? FeedbackProviderKey)
{
    private const string FeedbackTempDataKey = "ProviderFeedback";
    private const string FeedbackProviderTempDataKey = "ProviderFeedbackKey";

    /// <summary>The page that shows the form of one provider.</summary>
    public string DetailUrl(string providerKey) => Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(ReturnUrl, "provider", providerKey);

    public ProviderView? Selected => Providers.FirstOrDefault(provider => provider.Key == SelectedKey);

    public ProviderFeedback FeedbackFor(ProviderView provider) => provider.Key == FeedbackProviderKey ? Feedback : ProviderFeedback.None;

    /// <summary>The localization key of the message of a feedback, or null for none.</summary>
    public static string? MessageKey(ProviderFeedback feedback) => feedback switch
    {
        ProviderFeedback.Saved => "admin.providers.saved",
        ProviderFeedback.SavedConnectionWorks => "admin.providers.savedConnectionWorks",
        ProviderFeedback.Removed => "admin.providers.removed",
        ProviderFeedback.InvalidInput => "admin.providers.invalidInput",
        ProviderFeedback.TestSucceeded => "admin.providers.test.succeeded",
        ProviderFeedback.TestSucceededUnsaved => "admin.providers.test.succeededUnsaved",
        ProviderFeedback.TestAuthenticationFailed => "admin.providers.test.authFailed",
        ProviderFeedback.TestUnreachable => "admin.providers.test.unreachable",
        ProviderFeedback.TestRateLimited => "admin.providers.test.rateLimited",
        ProviderFeedback.TestNotConfigured => "admin.providers.test.notConfigured",
        _ => null
    };

    public static bool IsFailure(ProviderFeedback feedback) => feedback is not (ProviderFeedback.None or ProviderFeedback.Saved or ProviderFeedback.SavedConnectionWorks or ProviderFeedback.Removed or ProviderFeedback.TestSucceeded or ProviderFeedback.TestSucceededUnsaved);

    /// <summary>Keeps the feedback for the one page load after the redirect that follows a post.</summary>
    public static void Remember(ITempDataDictionary tempData, string providerKey, ProviderFeedback feedback)
    {
        tempData[FeedbackTempDataKey] = feedback.ToString();
        tempData[FeedbackProviderTempDataKey] = providerKey;
    }

    public static (ProviderFeedback Feedback, string? Key) Take(ITempDataDictionary tempData) =>
        tempData[FeedbackTempDataKey] is string value && Enum.TryParse<ProviderFeedback>(value, out var feedback) ? (feedback, tempData[FeedbackProviderTempDataKey] as string) : (ProviderFeedback.None, null);
}
