using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Library
{
    internal sealed class StoredFileBatchData : BaseData
    {
        public StoredFileBatchData(long libraryRootId, DateTime seenAt, string files)
        {
            LibraryRootId = libraryRootId;
            SeenAt = seenAt;
            Files = files;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.LastSeenAt))]
        public DateTime SeenAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Payload))]
        public string Files { get; }
    }

    internal sealed class StoredFileProbeData : BaseData
    {
        public StoredFileProbeData(long id, long sizeBytes, DateTime modifiedAt, DateTime probedAt, string? container, long? durationMs, long? bitRate, string tracks)
        {
            Id = id;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            ProbedAt = probedAt;
            Container = container;
            DurationMs = durationMs;
            BitRate = bitRate;
            Tracks = tracks;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ProbedAt), true)]
        public DateTime ProbedAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Container))]
        public string? Container { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DurationMs))]
        public long? DurationMs { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.BitRate))]
        public long? BitRate { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Payload))]
        public string Tracks { get; }
    }

    internal sealed class StoredFileCandidateData : BaseData
    {
        public StoredFileCandidateData(long id, string relativePath, long sizeBytes, DateTime modifiedAt, long? workId)
        {
            Id = id;
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            WorkId = workId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }
    }

    internal sealed class StoredFileParameters : BaseData
    {
        public StoredFileParameters(long storedFileId)
        {
            StoredFileId = storedFileId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }
    }

    internal sealed class StoredFileLockData : BaseData
    {
        public StoredFileLockData(long id, long libraryRootId, string relativePath, long? mediaAssetId, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId)
        {
            Id = id;
            LibraryRootId = libraryRootId;
            RelativePath = relativePath;
            MediaAssetId = mediaAssetId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id), false)]
        public long? MediaAssetId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }
    }

    internal sealed class StoredFileAssignmentData : BaseData
    {
        public StoredFileAssignmentData(long storedFileId, long? workId, string? title, WorkUnitType? workUnitTypeId, decimal? unitNumber, decimal? parentUnitNumber, long? gamePlatformId)
        {
            GamePlatformId = gamePlatformId;
            StoredFileId = storedFileId;
            WorkId = workId;
            Title = title;
            WorkUnitTypeId = workUnitTypeId;
            UnitNumber = unitNumber;
            ParentUnitNumber = parentUnitNumber;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle), false)]
        public string? Title { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId), false)]
        public WorkUnitType? WorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? UnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? ParentUnitNumber { get; }

        // A game's platform; without one its platform folder names it.
        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Id), false)]
        public long? GamePlatformId { get; }
    }

    internal sealed class MediaAssetIdData : BaseData
    {
        public MediaAssetIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long Id { get; }
    }

    internal sealed class LibraryFileRowData : BaseData
    {
        public LibraryFileRowData(long id, long libraryRootId, string libraryRootName, MediaType mediaTypeId, string relativePath, long sizeBytes, DateTime? missingSince,
            long? mediaAssetId, long? workId, string? workTitle, string? romName)
        {
            RomName = romName;
            Id = id;
            LibraryRootId = libraryRootId;
            LibraryRootName = libraryRootName;
            MediaTypeId = mediaTypeId;
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            MissingSince = missingSince;
            MediaAssetId = mediaAssetId;
            WorkId = workId;
            WorkTitle = workTitle;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string LibraryRootName { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.MissingSince))]
        public DateTime? MissingSince { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id), false)]
        public long? MediaAssetId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle), false)]
        public string? WorkTitle { get; }

        // The verified dump a game file's hash names.
        [DataField(typeof(GameRomFields), nameof(GameRomFields.Name), false)]
        public string? RomName { get; }
    }

    internal sealed class LibraryFileFilterData : BaseData
    {
        public LibraryFileFilterData(long? libraryRootId, bool onlyUnmatched)
        {
            LibraryRootId = libraryRootId;
            OnlyUnmatched = onlyUnmatched;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.OnlyUnmatched))]
        public bool OnlyUnmatched { get; }
    }

    internal sealed class StoredFileCursorData : BaseData
    {
        public StoredFileCursorData(long libraryRootId, long afterId)
        {
            LibraryRootId = libraryRootId;
            AfterId = afterId;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.AfterId))]
        public long AfterId { get; }
    }

    internal sealed class WorkTitleLookupData : BaseData
    {
        public WorkTitleLookupData(string[] titles)
        {
            Titles = titles;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Titles))]
        public string[] Titles { get; }
    }

    internal sealed class WorkTitleMatchData : BaseData
    {
        public WorkTitleMatchData(string title, MediaType mediaTypeId, long workId)
        {
            Title = title;
            MediaTypeId = mediaTypeId;
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    internal sealed class StoredFileIdData : BaseData
    {
        public StoredFileIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }
    }

    internal sealed class TrickplaySaveData : BaseData
    {
        public TrickplaySaveData(long id, long sizeBytes, DateTime modifiedAt, int? trickplayFrames)
        {
            Id = id;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            TrickplayFrames = trickplayFrames;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.TrickplayFrames), false)]
        public int? TrickplayFrames { get; }
    }

    // A video file that has no seek thumbnails yet.
    internal sealed class TrickplaySourceData : BaseData
    {
        public TrickplaySourceData(long id, long sizeBytes, DateTime modifiedAt, long durationMs, string relativePath, string rootRelativePath, long storageMountId,
            StorageProtocolType storageProtocolTypeId, string sharePath)
        {
            Id = id;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            DurationMs = durationMs;
            RelativePath = relativePath;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
            StorageProtocolTypeId = storageProtocolTypeId;
            SharePath = sharePath;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DurationMs))]
        public long DurationMs { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootRelativePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.StorageProtocolTypeId))]
        public StorageProtocolType StorageProtocolTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.SharePath))]
        public string SharePath { get; }
    }
}
