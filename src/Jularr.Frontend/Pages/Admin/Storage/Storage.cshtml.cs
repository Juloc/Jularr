using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Storage
{
    [Authorize(Policy = ServiceApi.StorageReadPolicyName)]
    public class StorageModel(ReadStorageMountsV1 read, ReadStorageMountV1 readMount, ReadStorageProtocolsV1 readProtocols, ReadStorageLocalPathsV1 readLocalPaths,
        CreateStorageMountV1 create, UpdateStorageMountV1 update, ExecuteStorageMountV1 execute, WakeStorageMountV1 wake, RemoveStorageMountV1 remove,
        ReadStorageDirectoriesV1 readDirectories, ReadLibraryRootsV1 readRoots, CreateLibraryRootV1 createRoot, RenameLibraryRootV1 renameRoot, DeleteLibraryRootV1 deleteRoot,
        QueueLibraryScanV1 queueScan, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<StorageMountDetailsV1>? _mounts;
        private StorageMountDetailsV1? _mount;
        private IReadOnlyList<StorageProtocolVersionDetailsV1> _protocols = [];
        private IReadOnlyList<StorageLocalPathV1> _localPaths = [];
        private PageResult<LibraryRootDetailsV1>? _roots;
        private IReadOnlyList<StorageDirectoryV1>? _directories;
        private SaveStorageMountDataV1? _input;
        private CreateLibraryRootDataV1? _rootInput;
        private string? _failedSection;
        private bool IsSetup => Request.Path.StartsWithSegments("/Setup");
        private string PageName => IsSetup ? "/Setup/Storage/Storage" : "/Admin/Storage/Storage";

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int RootPageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public long? StorageMountId { get; set; }

        [BindProperty(SupportsGet = true)]
        public StorageProtocolType Protocol { get; set; } = StorageProtocolType.Smb;

        [BindProperty(SupportsGet = true)]
        public string? Browse { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? LibraryRootId { get; set; }

        protected override void Define(Page page)
        {
            page.Title(IsSetup ? "setup.storage.title" : "storage.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Storage/Storage.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            var split = new Split(content =>
            {
                // Physical connections.
                content.Primary.Add(new A(new Text("storage.add.Smb")).Attribute("href", Address(protocol: StorageProtocolType.Smb)));
                content.Primary.Add(new A(new Text("storage.add.Nfs")).Attribute("href", Address(protocol: StorageProtocolType.Nfs)));
                if (_localPaths.Count > 0) content.Primary.Add(new A(new Text("storage.add.Local")).Attribute("href", Address(protocol: StorageProtocolType.Local)));
                content.Primary.Add(new Table<StorageMountDetailsV1>(_mounts?.Items ?? [])
                    .Text(item => item.Name, "storage.name")
                    .Resource(item => item.StorageProtocolTypeId, "storage.protocol", protocol => "storage.protocol." + protocol)
                    .Content("storage.location", item => new ValueText(item.Host is null ? item.SharePath : item.Host + item.SharePath))
                    .Link(item => item.Id, "common.open", id => Address(id), "common.open"));

                if (_mounts?.Page > 1) content.Primary.Add(new A(new Text("common.previous")).Attribute("href", Address(pageNumber: PageNumber - 1)));
                if (_mounts is { } rows && rows.Items.Count == rows.PageSize) content.Primary.Add(new A(new Text("common.next")).Attribute("href", Address(pageNumber: PageNumber + 1)));

                // Shared settings/setup editor.
                var protocol = _input?.StorageProtocolTypeId ?? _mount?.StorageProtocolTypeId ?? Protocol;
                content.Secondary.Add(ConnectionForm(protocol, csrf));
                if (protocol != StorageProtocolType.Local)
                {
                    content.Secondary.Add(new P(new Text("storage.credentialsHint")));
                    content.Secondary.Add(new P(new Text("storage.applyHint")));
                }

                if (_mount is { } mount)
                {
                    content.Secondary.Add(new Div([.. ConnectionActions(mount, csrf)]).Class("storage__actions"));
                    if (TempData["StorageMounted"] is bool isMounted)
                    {
                        content.Secondary.Add(new P(new Text(isMounted ? "storage.mounted" : "storage.unmounted")).Attribute("role", "status"));
                        if (isMounted && TempData["StorageCurrent"] is false) content.Secondary.Add(new P(new Text("storage.stale")).Attribute("role", "status"));
                    }

                    if (TempData["StorageWoken"] is true) content.Secondary.Add(new P(new Text("storage.wake.sent")).Attribute("role", "status"));
                    if (TempData["StorageScanQueued"] is true) content.Secondary.Add(new P(new Text("storage.paths.scanQueued")).Attribute("role", "status"));
                    if (FormError is not null && _failedSection is not ("Save" or "Root")) content.Secondary.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                    content.Secondary.Add(new UiSection("storage.paths", Paths(mount, csrf)));
                }
            });
            var detailOpen = StorageMountId is not null || _input is not null || Request.Query.ContainsKey("protocol");
            page.Content.Add(split.MasterDetail(detailOpen, Address()));
            if (IsSetup) page.Content.Add(new A(new Text("admin.instance.title")).Attribute("href", "/Admin/Instance"));
        }

        private Form<SaveStorageMountDataV1> ConnectionForm(StorageProtocolType protocol, string? csrf)
        {
            var versions = _protocols.Where(item => item.StorageProtocolTypeId == protocol)
                .Select(item => new SelectOption(item.ProtocolVersion, item.ProtocolVersion)).ToArray();
            var input = _input ?? (_mount is { } mount
                ? new SaveStorageMountDataV1(mount.Name, mount.StorageProtocolTypeId, mount.Host, mount.SharePath, mount.ProtocolVersion, mount.Username, mount.Domain, mount.IsReadOnly, null,
                    mount.WakeMacAddress, mount.WakeBroadcastAddress, mount.WakeOnDemand)
                : new SaveStorageMountDataV1("", protocol, protocol == StorageProtocolType.Local ? null : "", "", versions.FirstOrDefault()?.Value, null, null, true, null));

            return new Form<SaveStorageMountDataV1>(form =>
            {
                form.Hidden(item => item.StorageProtocolTypeId);
                form.Text(item => item.Name, "storage.name");
                if (protocol == StorageProtocolType.Local)
                {
                    form.Select(item => item.SharePath, "storage.localPath", _localPaths.Select(path => new SelectOption(path.LocalPath, path.LocalPath)));
                }
                else
                {
                    form.Text(item => item.Host, "storage.host");
                    form.Text(item => item.SharePath, "storage.sharePath");
                    form.Select(item => item.ProtocolVersion, "storage.protocolVersion", versions);
                }

                if (protocol == StorageProtocolType.Smb)
                {
                    form.Text(item => item.Username, "storage.username");
                    form.Text(item => item.Domain, "storage.domain");
                    form.Password(item => item.Password, "storage.password", "new-password");
                }

                form.Select(item => item.IsReadOnly, "storage.access", [
                    new SelectOption("True", texts["storage.readOnly"]), new SelectOption("False", texts["storage.readWrite"])
                ]);

                // Wake-on-LAN policy.
                form.Text(item => item.WakeMacAddress, "storage.wake.macAddress");
                form.Text(item => item.WakeBroadcastAddress, "storage.wake.broadcastAddress");
                form.Select(item => item.WakeOnDemand, "storage.wake.policy", [
                    new SelectOption("False", texts["storage.wake.ownerOnly"]), new SelectOption("True", texts["storage.wake.onDemand"])
                ]);
                form.Submit("common.save");
            }, Address(StorageMountId), csrf, input, _failedSection == "Save" ? FormError : null);
        }

        private IEnumerable<IUiComponent> ConnectionActions(StorageMountDetailsV1 mount, string? csrf)
        {
            // Explicit mount actions only.
            var operations = mount.CanMount ? Enum.GetValues<StorageMountOperationType>() : [StorageMountOperationType.ReadStatus];
            foreach (var operation in operations)
            {
                yield return new Form<ExecuteStorageMountDataV1>(form =>
                {
                    form.Hidden(item => item.OperationTypeId);
                    form.Submit("storage.operation." + operation);
                }, Address(mount.Id, handler: "Connection"), csrf, new ExecuteStorageMountDataV1(operation));
            }

            if (mount.WakeMacAddress is not null)
            {
                yield return new Form<StorageMountParametersV1>(form => form.Submit("storage.wake.send"), Address(mount.Id, handler: "Wake"), csrf, new StorageMountParametersV1(mount.Id));
            }

            yield return new Form<RemoveStorageMountDataV1>(form =>
            {
                form.Hidden(item => item.Version);
                form.Submit("storage.remove");
            }, Address(mount.Id, handler: "Remove"), csrf, new RemoveStorageMountDataV1(mount.Version));
        }

        private IReadOnlyList<IUiComponent> Paths(StorageMountDetailsV1 mount, string? csrf)
        {
            var items = new List<IUiComponent>();
            var roots = _roots?.Items ?? [];
            if (roots.Count > 0)
            {
                items.Add(new Table<LibraryRootDetailsV1>(roots)
                    .Text(item => item.Name, "storage.name")
                    .Content("table.type", item => new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)))
                    .Content("storage.paths.path", item => new ValueText("/" + item.RelativePath))
                    .Text(item => item.FileCount, "storage.paths.files")
                    .Content("common.actions", item => new Div(
                        new Form<LibraryRootParametersV1>(form =>
                        {
                            form.Hidden(root => root.LibraryRootId);
                            form.Submit("storage.paths.scan");
                        }, Address(mount.Id, handler: "Scan"), csrf, new LibraryRootParametersV1(item.Id)),
                        new A(new Text("common.edit")).Attribute("href", Address(mount.Id, libraryRootId: item.Id)),
                        new Form<DeleteLibraryRootParametersV1>(form =>
                        {
                            form.Hidden(root => root.LibraryRootId);
                            form.Hidden(root => root.Version);
                            form.Submit("storage.paths.remove");
                        }, Address(mount.Id, handler: "DeleteRoot"), csrf, new DeleteLibraryRootParametersV1(item.Id, item.Version)))));
                items.Add(new P(new Text("storage.paths.removeHint")));
            }

            // Paths have independent paging.
            if (_roots is { } rootPage)
            {
                if (rootPage.Page > 1) items.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(mount.Id, rootPageNumber: rootPage.Page - 1)));
                if (rootPage.Items.Count == rootPage.PageSize) items.Add(new A(new Text("common.next")).Class("button").Attribute("href", Address(mount.Id, rootPageNumber: rootPage.Page + 1)));
            }

            // Rename keeps location and media type.
            if (roots.FirstOrDefault(root => root.Id == LibraryRootId) is { } selected)
            {
                items.Add(new Form<RenameLibraryRootDataV1>(form =>
                {
                    form.Text(item => item.Name, "storage.name");
                    form.Hidden(item => item.Version);
                    form.Submit("common.save");
                }, Address(mount.Id, handler: "RenameRoot", libraryRootId: selected.Id), csrf, new RenameLibraryRootDataV1(selected.Name, selected.Version), _failedSection == "Root" ? FormError : null));
                return items;
            }

            // Safe folder choice below this connection.
            var path = _rootInput?.RelativePath ?? Browse ?? "";
            var trail = new List<IUiComponent> { new A(new Text("storage.paths.connectionRoot")).Attribute("href", Address(mount.Id, browse: "")) };
            var segments = path.Length == 0 ? [] : path.Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                trail.Add(new A(new ValueText(segments[index])).Attribute("href", Address(mount.Id, browse: string.Join('/', segments[..(index + 1)]))));
            }

            items.Add(new Nav([.. trail]).Class("storage__trail").Attribute("aria-label", texts["storage.paths.selected"]));
            if (_directories is { } directories)
            {
                items.Add(directories.Count == 0
                    ? new P(new Text("storage.paths.noFolders"))
                    : new Ul([.. directories.Select(directory => new Li(new A(new ValueText(directory.Name))
                        .Attribute("href", Address(mount.Id, browse: path.Length == 0 ? directory.Name : path + "/" + directory.Name))))]));
            }
            else
            {
                items.Add(new A(new Text("storage.paths.browse")).Attribute("href", Address(mount.Id, browse: path)));
            }

            var mediaTypes = Enum.GetValues<MediaType>().Select(type => new SelectOption(((byte)type).ToString(CultureInfo.InvariantCulture), texts["media.type." + type]));
            SelectOption[] subtypes = [new SelectOption("", texts["media.subtype.none"]), .. Enum.GetValues<MediaSubtype>()
                .Select(subtype => new SelectOption(((byte)subtype).ToString(CultureInfo.InvariantCulture), texts["media.subtype." + subtype]))];
            items.Add(new Form<CreateLibraryRootDataV1>(form =>
            {
                form.Hidden(item => item.StorageMountId);
                form.Hidden(item => item.RelativePath);
                form.Text(item => item.Name, "storage.name");
                form.Select(item => item.MediaTypeId, "table.type", mediaTypes);
                form.Select(item => item.MediaSubtypeId, "media.subtype", subtypes);
                form.Submit("storage.paths.add");
            }, Address(mount.Id, handler: "Root", browse: path), csrf, _rootInput ?? new CreateLibraryRootDataV1("", MediaType.Movie, mount.Id, path), _failedSection == "Root" ? FormError : null));
            return items;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? name, StorageProtocolType storageProtocolTypeId, string? host, string? sharePath, string? protocolVersion,
            string? username, string? domain, string? password, [BindRequired] bool isReadOnly, string? wakeMacAddress, string? wakeBroadcastAddress, [BindRequired] bool wakeOnDemand)
        {
            _failedSection = "Save";
            _input = new SaveStorageMountDataV1(name ?? "", storageProtocolTypeId, Blank(host), sharePath ?? "", Blank(protocolVersion), Blank(username), Blank(domain), isReadOnly, Blank(password),
                Blank(wakeMacAddress)?.ToUpperInvariant(), Blank(wakeBroadcastAddress), wakeOnDemand);
            if (StorageMountId is { } id)
            {
                await update.ExecuteAsync<UpdateResult>(new StorageMountParametersV1(id), _input, HttpContext.RequestAborted);
            }
            else
            {
                StorageMountId = (await create.ExecuteAsync<CreateStorageMountResultV1>(_input, HttpContext.RequestAborted)).Id;
            }

            return LocalRedirect(Address(StorageMountId));
        }

        public async Task<IActionResult> OnPostConnectionAsync([BindRequired] long storageMountId, StorageMountOperationType operationTypeId)
        {
            var result = await execute.ExecuteAsync<StorageMountStatusV1>(new StorageMountParametersV1(storageMountId),
                new ExecuteStorageMountDataV1(operationTypeId), HttpContext.RequestAborted);
            TempData["StorageMounted"] = result.IsMounted;
            TempData["StorageCurrent"] = result.IsCurrentConfiguration;
            return LocalRedirect(Address(storageMountId));
        }

        public async Task<IActionResult> OnPostWakeAsync([BindRequired] long storageMountId)
        {
            await wake.ExecuteAsync<UpdateResult>(new StorageMountParametersV1(storageMountId), default, HttpContext.RequestAborted);
            TempData["StorageWoken"] = true;
            return LocalRedirect(Address(storageMountId));
        }

        public async Task<IActionResult> OnPostRemoveAsync([BindRequired] long storageMountId, [BindRequired] long version)
        {
            await remove.ExecuteAsync<UpdateResult>(new StorageMountParametersV1(storageMountId), new RemoveStorageMountDataV1(version), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRootAsync([BindRequired] long storageMountId, string? name, [BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? relativePath)
        {
            _failedSection = "Root";
            _rootInput = new CreateLibraryRootDataV1(name ?? "", mediaTypeId, storageMountId, relativePath ?? "", mediaSubtypeId);
            await createRoot.ExecuteAsync<CreateLibraryRootResultV1>(_rootInput, HttpContext.RequestAborted);
            return LocalRedirect(Address(storageMountId));
        }

        public async Task<IActionResult> OnPostRenameRootAsync([BindRequired] long storageMountId, [BindRequired] long libraryRootId, string? name, [BindRequired] long version)
        {
            _failedSection = "Root";
            await renameRoot.ExecuteAsync<UpdateResult>(new LibraryRootParametersV1(libraryRootId), new RenameLibraryRootDataV1(name ?? "", version), HttpContext.RequestAborted);
            return LocalRedirect(Address(storageMountId));
        }

        public async Task<IActionResult> OnPostDeleteRootAsync([BindRequired] long storageMountId, [BindRequired] long libraryRootId, [BindRequired] long version)
        {
            await deleteRoot.ExecuteAsync<UpdateResult>(new DeleteLibraryRootParametersV1(libraryRootId, version), default, HttpContext.RequestAborted);
            return LocalRedirect(Address(storageMountId));
        }

        public async Task<IActionResult> OnPostScanAsync([BindRequired] long storageMountId, [BindRequired] long libraryRootId)
        {
            await queueScan.ExecuteAsync<QueueLibraryScanResultV1>(new LibraryRootParametersV1(libraryRootId), default, HttpContext.RequestAborted);
            TempData["StorageScanQueued"] = true;
            return LocalRedirect(Address(storageMountId));
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _mounts = await read.ExecuteAsync<PageResult<StorageMountDetailsV1>>(new ReadStorageMountsParametersV1(PageNumber), ct);
            _protocols = await readProtocols.ExecuteAsync<BaseDataList<StorageProtocolVersionDetailsV1>>(new ReadStorageProtocolsParametersV1(), ct);
            _localPaths = await readLocalPaths.ExecuteAsync<BaseDataList<StorageLocalPathV1>>(new ReadStorageLocalPathsParametersV1(), ct);
            if (StorageMountId is not { } id)
            {
                return;
            }

            _mount = await readMount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(id), ct);
            _roots = await readRoots.ExecuteAsync<PageResult<LibraryRootDetailsV1>>(new ReadLibraryRootsParametersV1(id, RootPageNumber), ct);

            // Storage I/O only on explicit browsing.
            if (Request.Query.ContainsKey("browse"))
            {
                var path = Browse ?? "";
                _directories = await readDirectories.ExecuteAsync<BaseDataList<StorageDirectoryV1>>(new StorageMountParametersV1(id), new ReadStorageDirectoriesDataV1(path), ct);
            }
        }

        private string Address(long? storageMountId = null, StorageProtocolType? protocol = null, int? pageNumber = null, string? handler = null, string? browse = null, long? libraryRootId = null, int? rootPageNumber = null)
        {
            var query = new Dictionary<string, string?>
            {
                ["pageNumber"] = (pageNumber ?? PageNumber).ToString(CultureInfo.InvariantCulture),
                ["rootPageNumber"] = (rootPageNumber ?? (storageMountId == StorageMountId ? RootPageNumber : 1)).ToString(CultureInfo.InvariantCulture),
                ["storageMountId"] = storageMountId?.ToString(CultureInfo.InvariantCulture),
                ["protocol"] = protocol?.ToString(),
                ["handler"] = handler,
                ["browse"] = browse,
                ["libraryRootId"] = libraryRootId?.ToString(CultureInfo.InvariantCulture)
            };
            return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(IsSetup ? "/Setup/Storage" : "/Admin/Storage", query.Where(pair => pair.Value is not null)!);
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
