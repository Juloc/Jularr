using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Work;

namespace Jularr.Frontend.Pages.Admin.Search
{
    // Sprache/Qualität (Jularr+): what the loaded releases show by the languages and the quality read from their names, or by a quality profile's allowed qualities and
    // languages ("Profil übernehmen"). Only the browser filters; nothing is asked again. It needs script, so without it the field is not shown.
    public sealed partial class SearchModel
    {
        // The languages offered to choose: the profiles' own, then the usual ones of the releases.
        private static readonly string[] UsualLanguages = ["de", "en", "ja", "fr", "es", "it"];

        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];

        private IUiComponent LanguageFilter()
        {
            var languages = _profiles.SelectMany(profile => profile.LanguageOrder).Select(code => code.ToLowerInvariant()).Where(code => code.Length == 2)
                .Concat(UsualLanguages).Distinct().Take(16).ToArray();
            IUiComponent Option(string type, string name, string value, IUiComponent label, bool chosen = false, params (string Name, string Value)[] data)
            {
                // Kept out of the search form: the filter only changes what the browser shows.
                var input = new Input().Attribute("type", type).Attribute(name, value).Attribute("form", "search-filter");
                if (type == "radio") input.Attribute("name", name);
                if (chosen) input.Attribute("checked", "checked");
                foreach (var (dataName, dataValue) in data) input.Attribute(dataName, dataValue);
                return new Label(input, new Span(label)).Class("search-menu__option");
            }

            var content = new List<IUiComponent>
            {
                new P(new Text("search.language.languages")).Class("search-menu__hint"),
                new Div([.. languages.Select(code => Option("checkbox", "data-filter-language", code, new ValueText(WorkLanguages.Name(WorkLanguages.Code(code)))))]).Class("search-menu__list"),
                new P(new Text("search.language.quality")).Class("search-menu__hint"),
                new Div(
                    Option("radio", "data-filter-resolution", "", new Text("search.language.quality.all"), true),
                    Option("radio", "data-filter-resolution", "720", new Text("search.language.quality.720")),
                    Option("radio", "data-filter-resolution", "1080", new Text("search.language.quality.1080")),
                    Option("radio", "data-filter-resolution", "2160", new Text("search.language.quality.2160"))).Class("search-menu__list")
            };
            if (_profiles.Count > 0)
            {
                content.Add(new P(new Text("search.language.profile")).Class("search-menu__hint"));
                content.Add(new Div([
                    Option("radio", "data-filter-profile", "", new Text("search.language.profile.none"), true),
                    .. _profiles.Select(profile => Option("radio", "data-filter-profile", profile.Id.ToString(CultureInfo.InvariantCulture), new ValueText(profile.Name), false,
                        ("data-qualities", string.Join('|', profile.AllowedQualities)), ("data-languages", string.Join(' ', profile.LanguageOrder.Select(code => code.ToLowerInvariant())))))])
                    .Class("search-menu__list"));
            }

            return new ActionMenu(new Span(new Span(new Text("search.language")).Class("search-bar__label"),
                    new Span(new Text("search.language.all")).Class("search-bar__value").Attribute("data-picker-value", "language")),
                [new Div([.. content]).Class("search-menu__filter")], "search-bar__picker search-bar__filter", "search.language");
        }
    }
}
