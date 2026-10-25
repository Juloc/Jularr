using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jularr.Integrations.Logic.Browser
{
    // The optional browser lives entirely below DataPath and never needs apt, sudo or the Docker socket.
    internal sealed class BrowserPackages(BrowserOptions options)
    {
        private const int MaximumMetadataBytes = 128 * 1024;
        private const int MaximumSizeScanFiles = 10_000;
        private const long MaximumArchiveBytes = 450L * 1024 * 1024;
        private const long MaximumExpandedBytes = 1_500L * 1024 * 1024;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string _root = Path.Combine(options.ProfilePath, "managed");
        private string _state = "disabled";
        private string? _error;
        private long _downloaded;
        private long _total;
        private long _retryAfterTicks;
        private int _installFailures;
        private int _refreshMode;
        private bool _enabled;
        private Func<CancellationToken, Task<string>> _stableVersion = LatestStableVersionAsync;

        internal BrowserPackages(BrowserOptions options, Func<CancellationToken, Task<string>> stableVersion) : this(options)
        {
            _stableVersion = stableVersion;
        }

        public bool Enabled { get => Volatile.Read(ref _enabled); set => Volatile.Write(ref _enabled, value); }
        public string State => Volatile.Read(ref _state);
        public string? Error => Volatile.Read(ref _error);
        public long DownloadedBytes => Interlocked.Read(ref _downloaded);
        public long TotalBytes => Interlocked.Read(ref _total);
        public string ExecutablePath => Path.Combine(_root, "current", "chrome-" + Platform(), "chrome");
        public bool IsInstalled => File.Exists(ExecutablePath) && VersionMatches(Path.Combine(_root, "current", ".jularr-version"));
        public string? InstalledVersion => IsInstalled ? File.ReadAllText(Path.Combine(_root, "current", ".jularr-version")).Trim() : null;
        public long ManagedSizeBytes
        {
            get
            {
                if (!Directory.Exists(_root)) return 0;
                try
                {
                    // Admin status reads must stay bounded and must not traverse a
                    // symbolic link into unrelated DataPath/library directories.
                    long total = 0;
                    var files = 0;
                    var scan = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    };
                    foreach (var file in Directory.EnumerateFiles(_root, "*", scan))
                    {
                        if (++files > MaximumSizeScanFiles) return 0; // Unknown, not a misleading partial size.
                        total = checked(total + new FileInfo(file).Length);
                    }
                    return total;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
                {
                    return 0;
                }
            }
        }
        // 1 checks Stable for updates, 2 forces reinstall even if the version is unchanged.
        public void RequestRefresh(bool repair)
        {
            Interlocked.Exchange(ref _refreshMode, repair ? 2 : 1);
            Interlocked.Exchange(ref _retryAfterTicks, 0);
            Interlocked.Exchange(ref _installFailures, 0);
            Volatile.Write(ref _error, null);
            Volatile.Write(ref _state, "queued");
        }
        public bool HasManagedData => Directory.Exists(_root);
        public bool CanRetryInstall => DateTime.UtcNow.Ticks >= Interlocked.Read(ref _retryAfterTicks);

        public static string? InstalledExecutable(string profilePath)
        {
            var root = Path.Combine(profilePath, "managed", "current");
            var path = Path.Combine(root, "chrome-" + Platform(), "chrome");
            return File.Exists(path) && VersionMatches(Path.Combine(root, ".jularr-version")) ? path : null;
        }

        private static bool VersionMatches(string path) => File.Exists(path)
            && Regex.IsMatch(File.ReadAllText(path).Trim(), @"^\d{2,4}(\.\d{1,5}){3}$", RegexOptions.CultureInvariant);

        public async Task InstallAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var refresh = Volatile.Read(ref _refreshMode);
                if (IsInstalled && refresh == 0)
                {
                    if (State != "failed") Volatile.Write(ref _state, "installed");
                    return;
                }

                if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(_root))
                {
                    throw new PlatformNotSupportedException("Managed Chrome requires a Linux container with an absolute DataPath.");
                }

                var stage = Path.Combine(_root, "staging");
                var archive = Path.Combine(_root, "download.part");
                var active = Path.Combine(_root, "current");
                var previous = Path.Combine(_root, "previous");
                Directory.CreateDirectory(_root);
                if (!Directory.Exists(active) && Directory.Exists(previous)) Directory.Move(previous, active);
                if (Directory.Exists(active) && Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
                var version = await _stableVersion(ct);
                if (refresh == 1 && IsInstalled && string.Equals(InstalledVersion, version, StringComparison.Ordinal))
                {
                    Interlocked.Exchange(ref _refreshMode, 0);
                    Volatile.Write(ref _state, "installed");
                    return;
                }
                try
                {
                    if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
                    if (File.Exists(archive)) File.Delete(archive);
                    Directory.CreateDirectory(stage);

                    Volatile.Write(ref _error, null);
                    Volatile.Write(ref _state, "downloading");
                    Interlocked.Exchange(ref _downloaded, 0);
                    Interlocked.Exchange(ref _total, 0);
                    await DownloadAsync(archive, version, ct);

                    Volatile.Write(ref _state, "extracting");
                    await ExtractAsync(archive, stage, ct);

                    var binary = Path.Combine(stage, "chrome-" + Platform(), "chrome");
                    if (!File.Exists(binary)) throw new InvalidDataException("The browser archive has no Chrome executable.");
                    File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                    await VerifyAsync(binary, ct);
                    await File.WriteAllTextAsync(Path.Combine(stage, ".jularr-version"), version, ct);
                    if (Directory.Exists(active)) Directory.Move(active, previous);
                    try
                    {
                        Directory.Move(stage, active);
                    }
                    catch
                    {
                        if (Directory.Exists(previous) && !Directory.Exists(active)) Directory.Move(previous, active);
                        throw;
                    }
                    if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
                    Interlocked.Exchange(ref _refreshMode, 0);
                    Interlocked.Exchange(ref _installFailures, 0);
                    Interlocked.Exchange(ref _retryAfterTicks, 0);
                    Volatile.Write(ref _state, "installed");
                }
                finally
                {
                    if (File.Exists(archive)) File.Delete(archive);
                    if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failures = Interlocked.Increment(ref _installFailures);
                var minutes = Math.Min(60, Math.Pow(2, Math.Min(6, failures - 1)));
                Interlocked.Exchange(ref _retryAfterTicks, DateTime.UtcNow.AddMinutes(minutes).Ticks);
                Volatile.Write(ref _error, exception.Message);
                Volatile.Write(ref _state, "failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RemoveAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                Enabled = false;
                var installedByJularr = Directory.Exists(_root);
                if (installedByJularr) Directory.Delete(_root, recursive: true);
                // All managed cache, cookies, downloads, and profile are inside _root.
                // Older/manual profiles outside _root are intentionally left untouched.
                Interlocked.Exchange(ref _installFailures, 0);
                Interlocked.Exchange(ref _retryAfterTicks, 0);
                Interlocked.Exchange(ref _refreshMode, 0);
                Volatile.Write(ref _error, null);
                Volatile.Write(ref _state, "disabled");
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task<string> LatestStableVersionAsync(CancellationToken ct)
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
                { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.GetAsync(
                "https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions-with-downloads.json",
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.OK ||
                response.Content.Headers.ContentLength is > MaximumMetadataBytes)
                throw new HttpRequestException("Chrome Stable metadata is unavailable.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var limited = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (limited.Length + count > MaximumMetadataBytes) throw new InvalidDataException("Chrome metadata is too large.");
                await limited.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            limited.Position = 0;
            using var json = await JsonDocument.ParseAsync(limited, cancellationToken: ct);
            var stable = json.RootElement.GetProperty("channels").GetProperty("Stable");
            var version = stable.GetProperty("version").GetString() ?? "";
            if (!Regex.IsMatch(version, @"^\d{2,4}(\.\d{1,5}){3}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Chrome Stable version is invalid.");
            var platform = Platform();
            var expected = "https://storage.googleapis.com/chrome-for-testing-public/" + version + "/" + platform + "/chrome-" + platform + ".zip";
            if (!stable.GetProperty("downloads").GetProperty("chrome").EnumerateArray().Any(asset =>
                asset.GetProperty("platform").GetString() == platform && asset.GetProperty("url").GetString() == expected))
                throw new InvalidDataException("Chrome Stable does not offer a package for this architecture.");
            return version;
        }

        private async Task DownloadAsync(string archive, string version, CancellationToken ct)
        {
            var platform = Platform();
            var url = new Uri("https://storage.googleapis.com/chrome-for-testing-public/" + version + "/" + platform + "/chrome-" + platform + ".zip");
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new HttpRequestException("Chrome download was not available.");
            if (response.Content.Headers.ContentLength is { } length && (length <= 0 || length > MaximumArchiveBytes))
                throw new InvalidDataException("Chrome archive size is outside the permitted limit.");

            Interlocked.Exchange(ref _total, response.Content.Headers.ContentLength ?? 0);
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buffer = new byte[65536];
            int received;
            while ((received = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (Interlocked.Add(ref _downloaded, received) > MaximumArchiveBytes)
                    throw new InvalidDataException("Chrome archive exceeds the permitted limit.");
                hash.AppendData(buffer, 0, received);
                await output.WriteAsync(buffer.AsMemory(0, received), ct);
            }

            if (response.Content.Headers.ContentLength is { } expected && DownloadedBytes != expected)
                throw new InvalidDataException("Chrome archive was truncated.");

            // Google Storage supplies a transport checksum; HTTPS to the fixed Google endpoint authenticates the publisher.
            if (response.Headers.TryGetValues("x-goog-hash", out var values))
            {
                var md5 = values.SelectMany(value => value.Split(',')).Select(value => value.Trim())
                    .FirstOrDefault(value => value.StartsWith("md5=", StringComparison.Ordinal));
                if (md5 is not null && !CryptographicOperations.FixedTimeEquals(
                    Convert.FromBase64String(md5[4..]), hash.GetHashAndReset()))
                    throw new InvalidDataException("Chrome archive checksum is invalid.");
            }
        }

        private static async Task ExtractAsync(string archive, string stage, CancellationToken ct)
        {
            using var zip = ZipFile.OpenRead(archive);
            var total = 0L;
            if (zip.Entries.Count > 50_000) throw new InvalidDataException("The browser archive has too many files.");
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith("chrome-" + Platform() + "/", StringComparison.Ordinal)
                    || name.Contains("..", StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal)
                    || name.StartsWith('/'))
                    throw new InvalidDataException("Unsafe browser archive path.");
                var kind = (entry.ExternalAttributes >> 16) & 0xF000;
                if (kind is 0xA000 or 0x6000) throw new InvalidDataException("Browser archive contains a link or device.");
                total = checked(total + entry.Length);
                if (total > MaximumExpandedBytes) throw new InvalidDataException("Browser extraction exceeds the permitted limit.");

                var destination = Path.GetFullPath(Path.Combine(stage, name));
                if (!destination.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("Browser archive path escapes the managed directory.");
                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var content = entry.Open();
                await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                await content.CopyToAsync(file, ct);
                if (OperatingSystem.IsLinux() && ((entry.ExternalAttributes >> 16) & 0x49) != 0)
                    File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        private static async Task VerifyAsync(string binary, CancellationToken ct)
        {
            using var process = Process.Start(new ProcessStartInfo(binary, "--version") { UseShellExecute = false, RedirectStandardOutput = true })
                ?? throw new InvalidDataException("Chrome could not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0 || !(await process.StandardOutput.ReadToEndAsync(ct)).Contains("Chrome", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Chrome executable failed the version check.");
        }

        private static string Platform() => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux64",
            Architecture.Arm64 => "linux-arm64",
            _ => throw new PlatformNotSupportedException("Managed Chrome supports Linux x64 and arm64.")
        };
    }
}
