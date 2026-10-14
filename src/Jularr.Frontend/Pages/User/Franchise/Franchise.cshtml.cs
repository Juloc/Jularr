using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Franchise
{
    // A film series' titles the profile may see.
    public sealed class FranchiseModel(ReadFranchiseV1 read, ReadFranchiseWorksV1 readWorks) : JularrPage
    {
        private FranchiseV1? _franchise;
        private IReadOnlyList<WorkCardV1> _works = [];

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            page.Title("franchise.title");
            page.Navigation("nav.library");
            page.Asset("Pages/User/Franchise/Franchise.entry.scss");
            if (_franchise is { } franchise)
            {
                page.Content.Add(new H2(new ValueText(franchise.Name)));
                page.Content.Add(new Div([.. _works.Select(item => new MediaCard(item.CanonicalTitle, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl,
                    item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)))]).Class("media-grid"));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _franchise = await read.ExecuteAsync<FranchiseV1>(new FranchiseParametersV1(Id), HttpContext.RequestAborted);
            _works = await readWorks.ExecuteAsync<BaseDataList<WorkCardV1>>(new FranchiseParametersV1(Id), HttpContext.RequestAborted);
        }
    }
}
