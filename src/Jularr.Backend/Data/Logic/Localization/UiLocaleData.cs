using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Logic.Localization
{
    internal sealed class UiLocaleParameters : BaseData
    {
        public UiLocaleParameters(long uiLocaleId)
        {
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }
    }

    internal sealed class UiLocaleData : BaseData
    {
        public UiLocaleData(long id)
        {
            Id = id;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long Id { get; }
    }
}
