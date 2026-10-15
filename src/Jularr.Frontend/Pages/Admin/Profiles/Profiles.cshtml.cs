using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Profiles
{
    // The quality profiles; each opens on its own page.
    public sealed class ProfilesModel(ReadAcquisitionProfilesV1 profiles, IAntiforgery antiforgery) : JularrPage
    {
        // Media types with release qualities that get new profiles here.
        internal static readonly (MediaType Type, MediaSubtype? Subtype)[] Kinds = [(MediaType.Movie, null), (MediaType.Series, null), (MediaType.Book, null), (MediaType.Book, MediaSubtype.LightNovel)];

        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];

        protected override void Define(Page page)
        {
            page.Title("admin.profiles.title");
            page.Navigation("nav.admin");
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(SetupStepType.Profiles, antiforgery.GetAndStoreTokens(HttpContext).RequestToken));
            var home = Home(Request);

            page.Content.Add(new Table<AcquisitionProfileDetailsV1>(_profiles)
                .Text(item => item.Name, "profile.name")
                .Content("table.type", item => new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)))
                .Content("profile.qualities", item => new ValueText(string.Join(", ", item.AllowedQualities)))
                .Content("profile.usage", item => item.IsDefault ? new Text("profile.default") : new ValueText(item.WantedCount.ToString(CultureInfo.InvariantCulture)))
                .Link(item => item.Id, "common.actions", id => home + "/" + id.ToString(CultureInfo.InvariantCulture), "common.edit"));
            page.Content.Add(new Div([.. Kinds.Select(kind => new A(new Text("profile.add." + (kind.Subtype?.ToString() ?? kind.Type.ToString()))).Class("button")
                .Attribute("href", home + "/New?type=" + kind.Type + (kind.Subtype is { } subtype ? "&subtype=" + subtype : "")))]).Class("page-actions"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync() =>
            _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), HttpContext.RequestAborted);

        internal static string Home(Microsoft.AspNetCore.Http.HttpRequest request) => SetupWizard.Address(request, SetupStepType.Profiles, "/Admin/Acquisition/Profiles");
    }
}
