using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;

namespace Jularr.Web.Features.Discovery;

/// <summary>
/// What the Filter control of the Home/Discover surface needs. The control lives in the shell's content header, outside the page body, so the
/// page hands it over through <c>ViewData</c> instead of the header knowing the page model.
/// </summary>
public sealed record DiscoverFilterView(DiscoverBrowseQuery Query, LibraryLanguagePreference Preference, UiTextBundle Ui);
