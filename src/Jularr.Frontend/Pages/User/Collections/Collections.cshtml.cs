using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Collection.V1;
using Jularr.Backend.Service.User.Collection.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Collections
{
    // The profile's collections and a new one.
    public sealed class CollectionsModel(ReadCollectionsV1 read, CreateCollectionV1 create, IAntiforgery antiforgery) : JularrPage
    {
        private IReadOnlyList<CollectionV1> _collections = [];
        private CreateCollectionDataV1? _input;

        protected override void Define(Page page)
        {
            page.Title("collections.title");
            page.Navigation("collections.title");
            page.Content.Add(_collections.Count > 0
                ? new Table<CollectionV1>(_collections)
                    .Content("collections.name", item => new A(new ValueText(item.Name)).Attribute("href", "/Collections/" + item.Id.ToString(CultureInfo.InvariantCulture)))
                    .Content("collections.kind", item => new Text(item.IsSmart ? "collections.smart" : "collections.manual"))
                    .Content("table.titles", item => new ValueText(item.WorkCount?.ToString(CultureInfo.CurrentCulture) ?? ""))
                : new P(new Text("collections.empty")));

            page.Content.Add(new UiSection("collections.new", [new Form<CreateCollectionDataV1>(form =>
            {
                form.Text(item => item.Name, "collections.name");
                form.Toggle(item => item.IsSmart, "collections.smartToggle");
                form.Submit("collections.create");
            }, "/Collections", antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input ?? new CreateCollectionDataV1("", false), FormError)]));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _collections = await read.ExecuteAsync<BaseDataList<CollectionV1>>(new CollectionsParametersV1(), HttpContext.RequestAborted);
        }

        public async Task<IActionResult> OnPostAsync(string? name, bool isSmart)
        {
            _input = new CreateCollectionDataV1(name ?? "", isSmart);
            var created = await create.ExecuteAsync<CreateCollectionResultV1>(new CollectionsParametersV1(), _input, HttpContext.RequestAborted);
            return LocalRedirect("/Collections/" + created.Id.ToString(CultureInfo.InvariantCulture));
        }
    }
}
