using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class GameRomCatalogNameData : BaseData
    {
        public GameRomCatalogNameData(string name)
        {
            Name = name;
        }

        [DataField(typeof(GameRomCatalogFields), nameof(GameRomCatalogFields.Name))]
        public string Name { get; }
    }

    internal sealed class GameRomCatalogStateData : BaseData
    {
        public GameRomCatalogStateData(DateTime importedAt)
        {
            ImportedAt = importedAt;
        }

        [DataField(typeof(GameRomCatalogFields), nameof(GameRomCatalogFields.ImportedAt))]
        public DateTime ImportedAt { get; }
    }

    // A platform's DAT replaces its earlier import; the platform is created when no provider named it yet.
    internal sealed class GameRomCatalogData : BaseData
    {
        public GameRomCatalogData(string name, int igdbId, string platformName, string abbreviation, string? version, DateTime importedAt, string roms)
        {
            Name = name;
            IgdbId = igdbId;
            PlatformName = platformName;
            Abbreviation = abbreviation;
            Version = version;
            ImportedAt = importedAt;
            Roms = roms;
        }

        [DataField(typeof(GameRomCatalogFields), nameof(GameRomCatalogFields.Name))]
        public string Name { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.IgdbId), true)]
        public int IgdbId { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Name))]
        public string PlatformName { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Abbreviation), true)]
        public string Abbreviation { get; }

        [DataField(typeof(GameRomCatalogFields), nameof(GameRomCatalogFields.Version))]
        public string? Version { get; }

        [DataField(typeof(GameRomCatalogFields), nameof(GameRomCatalogFields.ImportedAt))]
        public DateTime ImportedAt { get; }

        [DataField(typeof(GameRomFields), nameof(GameRomFields.Payload))]
        public string Roms { get; }
    }

    // Files changed since they were read keep no hash.
    internal sealed class StoredFileHashData : BaseData
    {
        public StoredFileHashData(long id, long sizeBytes, DateTime modifiedAt, DateTime probedAt, string? sha1)
        {
            Id = id;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            ProbedAt = probedAt;
            Sha1 = sha1;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ProbedAt), true)]
        public DateTime ProbedAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Sha1))]
        public string? Sha1 { get; }
    }

    // The next game files of a root a scan hashes.
    internal sealed class GameRomCursorData : BaseData
    {
        public GameRomCursorData(long libraryRootId, long afterId, string pattern, long maximumBytes)
        {
            LibraryRootId = libraryRootId;
            AfterId = afterId;
            Pattern = pattern;
            MaximumBytes = maximumBytes;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.AfterId))]
        public long AfterId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Payload))]
        public string Pattern { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long MaximumBytes { get; }
    }

    // An unassigned file whose hash a DAT names.
    internal sealed class GameRomFileData : BaseData
    {
        public GameRomFileData(long storedFileId, string romName)
        {
            StoredFileId = storedFileId;
            RomName = romName;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(GameRomFields), nameof(GameRomFields.Name))]
        public string RomName { get; }
    }
}
