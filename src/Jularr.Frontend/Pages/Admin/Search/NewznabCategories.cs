using System.Globalization;

namespace Jularr.Frontend.Pages.Admin.Search
{
    // The standard Newznab categories, as Prowlarr names them; an indexer's own numbers (100000 and up) are left out of the list.
    internal static class NewznabCategories
    {
        private static readonly HashSet<int> Standard =
        [
            1000, 1010, 1020, 1030, 1040, 1050, 1060, 1070, 1080, 1090, 1110, 1120, 1130, 1140, 1180,
            2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060, 2070, 2080,
            3000, 3010, 3020, 3030, 3040, 3050, 3060,
            4000, 4010, 4020, 4030, 4040, 4050, 4060, 4070,
            5000, 5010, 5020, 5030, 5040, 5045, 5050, 5060, 5070, 5080, 5090,
            6000, 6010, 6020, 6030, 6040, 6045, 6050, 6060, 6070, 6080, 6090,
            7000, 7010, 7020, 7030, 7040, 7050, 7060,
            8000, 8010, 8020
        ];

        public static bool Known(int category) => Standard.Contains(category);

        public static string Key(int category) => "newznab.category." + category.ToString(CultureInfo.InvariantCulture);
    }
}
