using System.Globalization;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;

namespace Jularr.Backend.Logic.Storage
{
    // A NAS share as a Docker volume: Docker mounts it when the container starts, at the path where Jularr looks for the connection.
    internal static class StorageVolumeLogic
    {
        // The image's app user owns what an SMB share shows.
        private const int AppUser = 1654;

        public static string ComposeBlock(StorageMountData mount, string mountRoot)
        {
            var id = mount.Id.ToString(CultureInfo.InvariantCulture);
            var target = mountRoot.TrimEnd('/') + "/" + id;
            string type, device;
            var options = new List<string>();
            if (mount.StorageProtocolTypeId == StorageProtocolType.Smb)
            {
                type = "cifs";
                device = "//" + mount.Host + Literal(mount.SharePath);
                options.Add("username=" + Literal(mount.Username!));
                options.Add("password=${JULARR_NAS_" + id + "_PASSWORD}");
                if (mount.Domain is { Length: > 0 } domain) options.Add("domain=" + Literal(domain));
                options.Add("vers=" + mount.ProtocolVersion);
                options.Add("uid=" + AppUser.ToString(CultureInfo.InvariantCulture));
                options.Add("gid=" + AppUser.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                // An unreachable NFS server returns errors instead of hanging the files Jularr reads.
                type = "nfs";
                device = ":" + Literal(mount.SharePath);
                options.Add("addr=" + mount.Host);
                options.Add("nfsvers=" + mount.ProtocolVersion);
                options.Add("soft");
            }

            if (mount.IsReadOnly) options.Add("ro");
            return $"""
                services:
                  jularr:
                    volumes:
                      - nas-{id}:{target}
                volumes:
                  nas-{id}:
                    driver_opts:
                      type: {type}
                      device: {Quote(device)}
                      o: {Quote(string.Join(",", options))}
                """;
        }

        // Compose reads $ as a variable; only the password reference stays one.
        private static string Literal(string value) => value.Replace("$", "$$", StringComparison.Ordinal);

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
