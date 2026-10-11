using System.Globalization;
using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Service.Core.Localization
{
    public interface IUiTextCatalog
    {
        Task<UiTextBundle> GetAsync(CultureInfo culture, CancellationToken cancellationToken = default);
        Task<UiTextBundle> GetByLocaleIdAsync(long uiLocaleId, CancellationToken cancellationToken = default);
    }
}
