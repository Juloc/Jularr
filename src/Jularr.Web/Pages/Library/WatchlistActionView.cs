using Jularr.Web.Features.Localization;

namespace Jularr.Web.Pages.Library;

/// <summary>The personal-state control of a title's hero: add to or remove from the profile's watchlist. <paramref name="Route"/> is where the page's handler returns to.</summary>
public sealed record WatchlistActionView(bool IsOn, IDictionary<string, string> Route, UiTextBundle Ui);
