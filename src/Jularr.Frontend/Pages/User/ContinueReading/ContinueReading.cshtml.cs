using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Service.User.Library.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Pages.User.ContinueReading
{
    // The Continue Reading bar as an HTML fragment: the most recent unfinished book or comic, or nothing.
    public sealed class ContinueReadingModel(ReadContinueV1 continuation, UiText texts, FrontendAssets assets) : PageModel
    {
        public async Task<IActionResult> OnGetAsync()
        {
            if (!texts.CanRead()) return new NoContentResult();

            var items = await continuation.ExecuteAsync<BaseDataList<ContinueItemV1>>(new ReadContinueParametersV1(), HttpContext.RequestAborted);
            if (items.FirstOrDefault(item => ReadableMedia.IsReadable(item.MediaAssetTypeId)) is not { } item) return new NoContentResult();

            var asset = item.MediaAssetId.ToString(CultureInfo.InvariantCulture);
            var key = asset + ":" + item.Progression?.ToString(CultureInfo.InvariantCulture);
            return Partial("_UiComponent", new ContinueReadingBar(assets, item.Title, item.PosterUrl,
                MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber), item.Progression, "/Read/" + asset, key));
        }
    }
}
