using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class LanguageAliasesData : BaseData
    {
        public LanguageAliasesData(string languageAliases)
        {
            LanguageAliases = languageAliases;
        }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.LanguageAliases))]
        public string LanguageAliases { get; }
    }
}
