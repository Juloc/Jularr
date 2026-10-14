using System.Globalization;
using Jularr.Backend.Data.Admin.Backup.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Backup.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Backup
{
    // Database backups of the instance; during setup a backup can replace the new instance.
    public sealed class BackupModel(ReadBackupsV1 read, QueueBackupV1 queue, DeleteBackupV1 delete, RestoreBackupV1 restore, IAntiforgery antiforgery) : JularrPage
    {
        private BackupsV1? _backups;

        private string Home => SetupWizard.IsActive(Request) ? "/Setup/Backup" : "/Admin/Backup";

        protected override void Define(Page page)
        {
            page.Title("admin.backup.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Backup/Backup.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            if (_backups is not { } backups) return;

            // State of the latest run.
            if (!backups.IsConfigured) page.Content.Add(new P(new Text("admin.backup.notConfigured")).Attribute("role", "status"));
            else if (backups.IsRunning) page.Content.Add(new P(new Text("admin.backup.running")).Attribute("role", "status"));
            else if (backups.ErrorKey is { } error) page.Content.Add(new P(new Text("admin.backup.failed"), new ValueText(" "), new Text("errors." + error)).Attribute("role", "alert"));
            if (backups.IsConfigured && !backups.IsRunning)
            {
                page.Content.Add(new Form<BackupsParametersV1>(form => form.Submit("admin.backup.create"), Home + "?handler=Create", csrf, new BackupsParametersV1()));
            }

            page.Content.Add(backups.Items.Count > 0
                ? new Table<BackupV1>(backups.Items)
                    .Content("admin.backup.createdAt", item => new ValueText(item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)))
                    .Content("table.size", item => new ValueText(ByteSize.Format(item.SizeBytes)))
                    .Content("admin.backup.compatible", item => new Text(item.IsCompatible ? "common.yes" : "admin.backup.otherVersion"))
                    .Content("common.actions", item => new Div([.. Actions(item, backups.CanRestore, csrf)]).Class("backup__actions"))
                : new P(new Text("admin.backup.empty")));
        }

        private IEnumerable<IUiComponent> Actions(BackupV1 item, bool canRestore, string? csrf)
        {
            if (canRestore && item.IsCompatible)
            {
                yield return new Form<BackupParametersV1>(form => form.Submit("admin.backup.restore"), Home + "?handler=Restore&name=" + item.Name, csrf, new BackupParametersV1(item.Name));
            }

            yield return new Form<BackupParametersV1>(form => form.Submit("admin.backup.delete"), Home + "?handler=Delete&name=" + item.Name, csrf, new BackupParametersV1(item.Name));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _backups = await read.ExecuteAsync<BackupsV1>(new BackupsParametersV1(), HttpContext.RequestAborted);
        }

        public async Task<IActionResult> OnPostCreateAsync()
        {
            await queue.ExecuteAsync<UpdateResult>(new BackupsParametersV1(), default, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostDeleteAsync(string name)
        {
            await delete.ExecuteAsync<UpdateResult>(new BackupParametersV1(name), default, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        // The restored instance has its own accounts; signing in starts over.
        public async Task<IActionResult> OnPostRestoreAsync(string name)
        {
            await restore.ExecuteAsync<UpdateResult>(new BackupParametersV1(name), default, HttpContext.RequestAborted);
            TempData["BackupRestored"] = true;
            return LocalRedirect("/Account/Login");
        }
    }
}
