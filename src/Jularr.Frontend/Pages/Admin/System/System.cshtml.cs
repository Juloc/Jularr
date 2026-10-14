using System.Globalization;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Service.Admin.System.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.System
{
    // Tools, queues, storage, backups and the library's size at a glance; links lead to the pages that act on them.
    public sealed class SystemModel(ReadSystemHealthV1 read, UiText texts) : JularrPage
    {
        private SystemHealthV1? _health;

        protected override void Define(Page page)
        {
            page.Title("system.title");
            page.Navigation("nav.admin");
            if (_health is not { } health) return;

            page.Content.Add(new FactList([
                ("system.database", new ValueText(ByteSize.Format(health.DatabaseBytes))),
                ("system.profiles", new ValueText(health.Profiles.ToString(CultureInfo.CurrentCulture))),
                ("system.queued", Link(health.QueuedOperations.ToString(CultureInfo.CurrentCulture), "/Admin/Activity")),
                ("system.failed", Link(health.FailedOperations.ToString(CultureInfo.CurrentCulture), "/Admin/Activity")),
                ("system.storageOffline", Link(health.StorageOffline.ToString(CultureInfo.CurrentCulture), "/Admin/Storage")),
                ("system.lastBackup", Link(health.LastBackupAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? texts["system.never"], "/Admin/Backup"))]));
            page.Content.Add(new UiSection("system.tools", [new Table<ToolStateV1>(health.Tools)
                .Content("system.tool", item => new Text("system.tool." + item.Key))
                .Content("table.status", item => new Text(item.IsAvailable ? "system.configured" : "system.missing"))]));
            page.Content.Add(new UiSection("nav.library", [new Table<MediaTypeCountV1>(health.Library)
                .Content("table.type", item => new Text("media.type." + item.MediaTypeId))
                .Content("table.titles", item => new ValueText(item.Count.ToString(CultureInfo.CurrentCulture)))
                .Content("table.size", item => new ValueText(ByteSize.Format(item.Bytes)))]));
        }

        private static IUiComponent Link(string value, string href) => new A(new ValueText(value)).Attribute("href", href);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _health = await read.ExecuteAsync<SystemHealthV1>(new ReadSystemHealthParametersV1(), HttpContext.RequestAborted);
        }
    }
}
