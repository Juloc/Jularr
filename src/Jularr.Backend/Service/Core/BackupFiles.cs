using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Service.Core
{
    // One folder per backup: the database dump, the key ring and the schema it belongs to.
    internal static partial class BackupFiles
    {
        public const string DumpName = "database.dump";
        public const string SealedKeysName = "keys.sealed";
        private const int KeyIterations = 600_000;
        private static readonly byte[] SealedMagic = "JKR1"u8.ToArray();
        private const string ManifestName = "backup.json";

        [GeneratedRegex(@"^jularr-\d{8}-\d{6}\z")]
        private static partial Regex Name();

        // Restores need the same schema; line endings differ between checkouts.
        public static string SchemaFingerprint { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(typeof(Service).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Jularr.Database.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var reader = new StreamReader(typeof(Service).Assembly.GetManifestResourceStream(name)!);
                return name + "\n" + reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            })))));

        public static string NewName(DateTime now) => "jularr-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        // The name carries the time; copies to another machine keep it.
        private static DateTime CreatedAt(string name) => DateTime.ParseExact(name["jularr-".Length..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        // Dumps and key copies are readable by the Jularr user only.
        public static string CreatePrivate(string path)
        {
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public static IReadOnlyList<BackupFolder> List(string root) =>
            [.. Folders(root)
                .Select(folder => new BackupFolder(folder.Name, CreatedAt(folder.Name), folder.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length),
                    ReadFingerprint(folder.FullName) == SchemaFingerprint, File.Exists(Path.Combine(folder.FullName, SealedKeysName))))
                .OrderByDescending(folder => folder.Name, StringComparer.Ordinal)];

        // Only the name is read; sizes and manifests stay untouched.
        public static DateTime? Newest(string root) => Folders(root).Select(folder => (DateTime?)CreatedAt(folder.Name)).Max();

        // Complete backups this class made.
        private static IEnumerable<DirectoryInfo> Folders(string root) => Directory.Exists(root)
            ? new DirectoryInfo(root).EnumerateDirectories("jularr-*").Where(folder => Name().IsMatch(folder.Name) && File.Exists(Path.Combine(folder.FullName, DumpName)))
            : [];

        // Only names this class created, never a path.
        public static string Resolve(string root, string name) =>
            Name().IsMatch(name) && Directory.Exists(Path.Combine(root, name)) ? Path.Combine(root, name) : throw new KeyNotFoundException("The backup was not found.");

        public static async Task WriteManifestAsync(string folder, DateTime createdAt, CancellationToken ct) =>
            await File.WriteAllTextAsync(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(new BackupManifest(SchemaFingerprint, createdAt)), ct);

        // The key ring as one file: a ZIP of its keys, AES-GCM with a key from the passphrase.
        public static void SealKeys(string? from, string file, string passphrase)
        {
            if (from is null || !Directory.Exists(from))
            {
                return;
            }

            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            {
                foreach (var key in Directory.EnumerateFiles(from, "*.xml"))
                {
                    zip.CreateEntryFromFile(key, Path.GetFileName(key));
                }
            }

            var plain = archive.ToArray();
            var salt = RandomNumberGenerator.GetBytes(16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var sealedKeys = new byte[plain.Length];
            var secret = Derive(passphrase, salt);
            try
            {
                using var aes = new AesGcm(secret, tag.Length);
                aes.Encrypt(nonce, plain, sealedKeys, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
                CryptographicOperations.ZeroMemory(plain);
            }

            File.WriteAllBytes(file, [.. SealedMagic, .. salt, .. nonce, .. tag, .. sealedKeys]);
        }

        // A wrong passphrase changes nothing.
        public static void UnsealKeys(string file, string? to, string passphrase)
        {
            // Without a key folder there is nothing to bring back, as with plain copies.
            if (to is null)
            {
                return;
            }

            var content = File.ReadAllBytes(file);
            if (content.Length < 48 || !content.AsSpan(0, 4).SequenceEqual(SealedMagic))
            {
                throw new CryptographicException("The key ring file is unreadable.");
            }

            var plain = new byte[content.Length - 48];
            var secret = Derive(passphrase, content[4..20]);
            try
            {
                using (var aes = new AesGcm(secret, 16))
                {
                    aes.Decrypt(content.AsSpan(20, 12), content.AsSpan(48), content.AsSpan(32, 16), plain);
                }

                CreatePrivate(to);
                using var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
                foreach (var entry in zip.Entries.Where(entry => entry.Name == entry.FullName && entry.Name.EndsWith(".xml", StringComparison.Ordinal)))
                {
                    var target = Path.Combine(to, entry.Name);
                    if (!File.Exists(target)) entry.ExtractToFile(target);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        private static byte[] Derive(string passphrase, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, KeyIterations, HashAlgorithmName.SHA256, 32);

        public static void CopyKeys(string? from, string? to)
        {
            if (from is null || to is null || !Directory.Exists(from))
            {
                return;
            }

            CreatePrivate(to);
            foreach (var key in Directory.EnumerateFiles(from, "*.xml").Where(key => !File.Exists(Path.Combine(to, Path.GetFileName(key)))))
            {
                File.Copy(key, Path.Combine(to, Path.GetFileName(key)));
            }
        }

        // At least the newest stays; leftovers of interrupted runs go too.
        public static void Prune(string root, int keep)
        {
            foreach (var old in List(root).Skip(Math.Max(1, keep)))
            {
                Directory.Delete(Path.Combine(root, old.Name), true);
            }

            foreach (var partial in Directory.EnumerateDirectories(root, "jularr-*.partial"))
            {
                Directory.Delete(partial, true);
            }
        }

        private static string? ReadFingerprint(string folder)
        {
            try
            {
                return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(folder, ManifestName)))?.SchemaFingerprint;
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                return null;
            }
        }

        private sealed class BackupManifest(string schemaFingerprint, DateTime createdAt)
        {
            public string SchemaFingerprint { get; } = schemaFingerprint;
            public DateTime CreatedAt { get; } = createdAt;
        }
    }

    internal sealed class BackupFolder(string name, DateTime createdAt, long sizeBytes, bool isCompatible, bool isSealed)
    {
        public string Name { get; } = name;
        public DateTime CreatedAt { get; } = createdAt;
        public long SizeBytes { get; } = sizeBytes;
        public bool IsCompatible { get; } = isCompatible;
        public bool IsSealed { get; } = isSealed;
    }
}
