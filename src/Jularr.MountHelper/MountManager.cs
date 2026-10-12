using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Integrations.Data.Nas;

namespace Jularr.MountHelper
{
    internal sealed class MountManager
    {
        private const string MountRoot = "/mnt/jularr";
        private const string StateRoot = "/var/lib/jularr-mount-helper";

        [SupportedOSPlatform("linux")]
        public async Task<NasMountResponse> ExecuteAsync(NasMountRequest request, CancellationToken ct)
        {
            Validate(request);
            var id = request.StorageMountId.ToString(CultureInfo.InvariantCulture);
            var target = MountRoot + "/" + id;
            var statePath = StateRoot + "/" + id + ".json";

            // Inspect kernel state only.
            var mount = File.ReadLines("/proc/self/mountinfo").Select(line => line.Split(' ')).FirstOrDefault(parts => DecodeMountPath(parts[4]) == target);
            MountedConfiguration? state = File.Exists(statePath) ? JsonSerializer.Deserialize<MountedConfiguration>(await File.ReadAllTextAsync(statePath, ct)) : null;
            if (request.Action == NasMountAction.Status)
            {
                return new NasMountResponse(mount is not null, mount is not null && Matches(request, mount, state) ? request.Version : 0);
            }

            if (mount is not null && (state is null || request.Action == NasMountAction.Connect && !Matches(request, mount, state)))
            {
                return new NasMountResponse(true, 0, "storage_configuration_changed");
            }

            if (request.Action == NasMountAction.Disconnect)
            {
                if (mount is not null && !await RunAsync(new ProcessStartInfo("/usr/bin/umount") { ArgumentList = { "--", target } }, ct))
                {
                    return new NasMountResponse(true, state!.Version, "storage_busy");
                }

                if (File.Exists(statePath)) File.Delete(statePath);
                return new NasMountResponse(false, 0);
            }

            if (mount is not null) return new NasMountResponse(true, request.Version);

            // Own the mount destination.
            Directory.CreateDirectory(MountRoot);
            Directory.CreateDirectory(target);
            if (new DirectoryInfo(MountRoot).LinkTarget is not null || new DirectoryInfo(target).LinkTarget is not null)
            {
                throw new ArgumentException("Mount targets cannot be symbolic links.");
            }

            // Keep credentials off argv.
            var credentialPath = "/run/jularr-mount-helper/credential-" + Guid.NewGuid().ToString("N");
            try
            {
                if (request.Protocol == "Smb")
                {
                    await using var file = new FileStream(credentialPath, new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    });
                    var credentials = Encoding.UTF8.GetBytes("username=" + request.Username + "\npassword=" + request.Password + "\ndomain=" + request.Domain + "\n");
                    try
                    {
                        await file.WriteAsync(credentials, ct);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(credentials);
                    }
                }

                // Persist ownership before mounting.
                Directory.CreateDirectory(StateRoot);
                File.SetUnixFileMode(StateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new MountedConfiguration(request.Version, Fingerprint(request))), ct);
                if (!await RunAsync(BuildMountCommand(request, target, credentialPath), ct))
                {
                    return new NasMountResponse(false, 0, "storage_unavailable");
                }

                return new NasMountResponse(true, request.Version);
            }
            finally
            {
                if (File.Exists(credentialPath)) File.Delete(credentialPath);
            }
        }

        internal static ProcessStartInfo BuildMountCommand(NasMountRequest request, string target, string credentialPath)
        {
            // Fixed protocol-specific options.
            var options = "nosuid,nodev,noexec," + (request.IsReadOnly ? "ro" : "rw") + ",vers=" + request.ProtocolVersion;
            if (request.Protocol == "Smb")
            {
                options += ",sec=ntlmssp,cache=strict,uid=1000,gid=1000,forceuid,forcegid,file_mode=0660,dir_mode=0770,credentials=" + credentialPath;
            }
            else
            {
                options += ",proto=tcp,hard,timeo=100,retrans=2";
            }

            return new ProcessStartInfo("/usr/bin/mount")
            {
                ArgumentList = { "-t", request.Protocol == "Smb" ? "cifs" : "nfs", "-o", options, "--", Source(request), target }
            };
        }

        internal static void Validate(NasMountRequest request)
        {
            // Bound the requested identity.
            if (!Enum.IsDefined(request.Action) || request.StorageMountId <= 0 || request.Version <= 0)
            {
                throw new ArgumentException("Invalid mount identity.");
            }

            // Restrict remote paths.
            if (request.Host is null || request.Host.Length > 253 || Uri.CheckHostName(request.Host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)
                || request.SharePath is null || request.SharePath.Length > 1024 || !request.SharePath.StartsWith('/') || request.SharePath.StartsWith("//")
                || request.SharePath.Any(character => char.IsControl(character) || character is '\\' or ',')
                || request.SharePath.Split('/').Any(segment => segment is "." or ".."))
            {
                throw new ArgumentException("Invalid NAS source.");
            }

            // Permit implemented protocols only.
            var supported = request.Protocol switch
            {
                "Smb" => request.ProtocolVersion is "3.1.1" or "3.0" && !string.IsNullOrWhiteSpace(request.Username) && request.SharePath != "/",
                "Nfs" => request.ProtocolVersion is "4.1" or "4.2" && request.Username is null && request.Domain is null && request.Password is null,
                _ => false
            };
            if (!supported)
            {
                throw new ArgumentException("Invalid NAS protocol.");
            }

            // Protect credential-file boundaries.
            if (new[] { request.Username, request.Domain, request.Password }.Any(value => value is not null && (value.Length > 1024 || value.Any(char.IsControl))))
            {
                throw new ArgumentException("Invalid NAS credentials.");
            }
        }

        private static string Source(NasMountRequest request) => request.Protocol == "Smb" ? "//" + request.Host + request.SharePath : request.Host + ":" + request.SharePath;
        private static string DecodeMountPath(string path) => path.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

        private static string Fingerprint(NasMountRequest request)
        {
            var identity = string.Join('\n', request.Protocol, request.Host, request.SharePath, request.ProtocolVersion, request.Username, request.Domain, request.IsReadOnly);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }

        private static bool Matches(NasMountRequest request, string[] mount, MountedConfiguration? state)
        {
            var separator = Array.IndexOf(mount, "-");
            return state?.Version == request.Version && state.Fingerprint == Fingerprint(request)
                && DecodeMountPath(mount[separator + 2]) == Source(request)
                && (request.Protocol == "Smb" ? mount[separator + 1] == "cifs" : mount[separator + 1] is "nfs" or "nfs4");
        }

        private static async Task<bool> RunAsync(ProcessStartInfo start, CancellationToken ct)
        {
            // Never invoke a shell.
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var process = Process.Start(start) ?? throw new IOException("Mount command could not start.");
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
            try
            {
                await process.WaitForExitAsync(ct);
                await Task.WhenAll(output, error);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }

        private sealed class MountedConfiguration(long version, string fingerprint)
        {
            public long Version { get; } = version;
            public string Fingerprint { get; } = fingerprint;
        }
    }
}
