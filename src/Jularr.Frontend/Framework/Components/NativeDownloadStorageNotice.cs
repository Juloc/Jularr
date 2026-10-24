using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Storage;

namespace Jularr.Frontend.Framework
{
    // Native download sources need writable staging on a confirmed filesystem.
    // This is a setup status, not a live mount-health claim: DownloadStorage.OpenAsync
    // remains responsible for testing mount identity, capacity and availability at grab time.
    public static class NativeDownloadStorageNotice
    {
        public static bool RequiresDirectStorage(AcquisitionConnectionType type) =>
            type is AcquisitionConnectionType.Gutenberg or AcquisitionConnectionType.Opds or AcquisitionConnectionType.AnnasArchive;

        public static IUiComponent Message(DownloadStorageArea area, string link = "/Admin/DownloadStorage") =>
            new Div(
                new Span(new Text(area == DownloadStorageArea.Direct ? "sources.storage.directMissing" : "sources.storage.usenetMissing")),
                new A(new Text("sources.storage.configure")).Class("button button--compact button--soft")
                    .Attribute("href", link)
            ).Class("native-storage-notice").Attribute("role", "status");
    }
}
