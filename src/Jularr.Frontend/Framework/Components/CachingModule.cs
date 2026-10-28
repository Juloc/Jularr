using System.Linq.Expressions;
using Jularr.Backend.Data.Common;

namespace Jularr.Frontend.Framework
{
    // The Caching module (key DiscoverCache) with what belongs to it, as the Modules page and the setup's module step show it inside the module's card:
    // the days extended data is kept, always, and while the module is on its sub-options in the tinted box: the full catalog (the LocalDiscovery module)
    // and detailing Discover in advance. Both forms save these with the switches.
    public static class CachingModule
    {
        public const string Key = "DiscoverCache";

        // A module shown as a sub-option of the Caching module instead of as a card of its own.
        public const string FullCatalogKey = "LocalDiscovery";

        public static void Parts<TData>(Form<TData> form, Expression<Func<TData, int>> keepDays, Expression<Func<TData, bool>> detailsAhead, Action<Form<TData>> fullCatalog, UiText texts)
            where TData : BaseData
        {
            form.Wrap(parts => new Div([.. parts]).Class("option-number"), field => field.Number(keepDays, "instance.caching.keepDays", affix: FieldAffix.Unit("unit.days")));
            form.Wrap(parts => new Div([.. parts]).Class("option-subs"), subs =>
            {
                fullCatalog(subs);
                subs.Switch(detailsAhead, "instance.caching.detailsDiscoverAhead", control => new OptionRow(texts["instance.caching.detailsDiscoverAhead"], null, control).Sub());
            });
        }

        // The full catalog's own switch, a row of the modules list.
        public static OptionRow FullCatalogRow(IUiComponent control, string[] requires, UiText texts) =>
            new OptionRow(texts["instance.module." + FullCatalogKey], null, control).Sub().Data("module", FullCatalogKey).Data("requires", string.Join(' ', requires));
    }
}
