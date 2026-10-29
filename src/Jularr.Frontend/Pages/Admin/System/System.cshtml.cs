using System.Globalization;
using System.Reflection;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Service.Admin.System.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.System
{
    // The server in numbers: the database, queues, storage and backups, the tools it runs and the library's size; links lead to the pages that act on them.
    // The Dashboard, Performance and Logs show how it is doing.
    public sealed class SystemModel(ReadSystemHealthV1 read, UiText texts) : JularrPage
    {
        // The release's version (the image build sets it from the tag); a build outside a release reports 1.0.0.
        private static readonly string Version = typeof(SystemModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

        private SystemHealthV1? _health;

        protected override void Define(Page page)
        {
            page.Title("system.title");
            page.Navigation("nav.admin");
            if (_health is not { } health) return;

            page.Content.Add(new FactList([
                ("table.version", new ValueText(Version)),
                ("system.database", new ValueText(ByteSize.Format(health.DatabaseBytes))),
                ("system.profiles", new ValueText(health.Profiles.ToString(CultureInfo.CurrentCulture))),
                ("system.queued", Link(health.QueuedOperations.ToString(CultureInfo.CurrentCulture), "/Admin/Activity")),
                ("system.failed", Link(health.FailedOperations.ToString(CultureInfo.CurrentCulture), "/Admin/Activity")),
                ("system.catalogTitles", new ValueText(health.CatalogWorks.ToString("N0", CultureInfo.CurrentCulture))),
                ("system.storageOffline", Link(health.StorageOffline.ToString(CultureInfo.CurrentCulture), "/Admin/Storage")),
                ("system.lastBackup", Link(health.LastBackupAt is { } backedUp ? new LocalTime(backedUp) : new ValueText(texts["system.never"]), "/Admin/Backup"))]));
            page.Content.Add(new UiSection("system.tools", [new Table<ToolStateV1>(health.Tools)
                .Content("system.tool", item => new Text("system.tool." + item.Key))
                .Content("table.status", item => new Text(item.IsAvailable ? "system.configured" : "system.missing"))]));
            page.Content.Add(new UiSection("nav.library", [new Table<MediaTypeCountV1>(health.Library)
                .Content("table.type", item => new Text("media.type." + item.MediaTypeId))
                .Content("table.titles", item => new ValueText(item.Count.ToString(CultureInfo.CurrentCulture)))
                .Content("table.size", item => new ValueText(ByteSize.Format(item.Bytes)))]));
        }

        private static IUiComponent Link(string value, string href) => Link(new ValueText(value), href);

        private static IUiComponent Link(IUiComponent value, string href) => new A(value).Attribute("href", href);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync() =>
            _health = await read.ExecuteAsync<SystemHealthV1>(new ReadSystemHealthParametersV1(), HttpContext.RequestAborted);
    }
}
