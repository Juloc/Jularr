using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Instance;
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

    internal sealed class StoredFilePagesData : BaseData
    {
        public StoredFilePagesData(long id, long sizeBytes, DateTime modifiedAt, int? pageCount)
        {
            Id = id;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            PageCount = pageCount;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.PageCount), false)]
        public int? PageCount { get; }
    }

    internal sealed class StoredFileProbeData : BaseData
    {
        public StoredFileProbeData(long id, long sizeBytes, DateTime modifiedAt, DateTime probedAt, string? container, long? durationMs, long? bitRate, string tracks,
            string chapters, string sidecarMarkers)
        {
            Id = id;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            ProbedAt = probedAt;
            Container = container;
            DurationMs = durationMs;
            BitRate = bitRate;
            Tracks = tracks;
            Chapters = chapters;
            SidecarMarkers = sidecarMarkers;
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

        // The file's chapters and the markers a sidecar file named, as JSON.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Payload))]
        public string Chapters { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Payload))]
        public string SidecarMarkers { get; }

        // The same analysis with other tracks, such as ones that carry the languages a person chose.
        public StoredFileProbeData WithTracks(string tracks) => new(Id, SizeBytes, ModifiedAt, ProbedAt, Container, DurationMs, BitRate, tracks, Chapters, SidecarMarkers);
    }

    // The languages a person chose for a file; empty where nothing was chosen.
    internal sealed class StoredFileChosenLanguagesData : BaseData
    {
        public StoredFileChosenLanguagesData(string[] chosenAudioLanguages, string[] chosenSubtitleLanguages)
        {
            ChosenAudioLanguages = chosenAudioLanguages;
            ChosenSubtitleLanguages = chosenSubtitleLanguages;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ChosenLanguages))]
        public string[] ChosenAudioLanguages { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ChosenLanguages))]
        public string[] ChosenSubtitleLanguages { get; }
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
        public StoredFileAssignmentData(long storedFileId, long? workId, string? title, WorkUnitType? workUnitTypeId, decimal? unitNumber, decimal? parentUnitNumber, long? gamePlatformId,
            bool isProtected)
        {
            IsProtected = isProtected;
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

        // Assigned or uploaded by an administrator; imports never replace it.
        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.IsProtected))]
        public bool IsProtected { get; }
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
            long? mediaAssetId, long? workId, string? workTitle, string? romName, bool hasOtherVersions, bool isProtected, MediaSubtype? mediaSubtypeId)
        {
            HasOtherVersions = hasOtherVersions;
            IsProtected = isProtected;
            RomName = romName;
            Id = id;
            LibraryRootId = libraryRootId;
            LibraryRootName = libraryRootName;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
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

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.HasOtherVersions))]
        public bool HasOtherVersions { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.IsProtected))]
        public bool IsProtected { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaSubtypeId), false)]
        public MediaSubtype? MediaSubtypeId { get; }
    }

    // The library one of a Work's files lies in.
    internal sealed class WorkLibraryRootData : BaseData
    {
        public WorkLibraryRootData(long libraryRootId)
        {
            LibraryRootId = libraryRootId;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }
    }

    // The media type and subtype of the library a file lies in.
    internal sealed class StoredFileRootData : BaseData
    {
        public StoredFileRootData(MediaType mediaTypeId, MediaSubtype? mediaSubtypeId)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaSubtypeId), false)]
        public MediaSubtype? MediaSubtypeId { get; }
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
        public TrickplaySourceData(long id, long sizeBytes, DateTime modifiedAt, long durationMs, string relativePath, string rootRelativePath, long storageMountId, int width, int height)
        {
            Width = width;
            Height = height;
            Id = id;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            DurationMs = durationMs;
            RelativePath = relativePath;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
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

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Width))]
        public int Width { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Height))]
        public int Height { get; }
    }

    // A video file whose subtitles are looked for, with what it already has.
    internal sealed class SubtitleSourceData : BaseData
    {
        public SubtitleSourceData(long id, long libraryRootId, long sizeBytes, DateTime modifiedAt, string relativePath, string? releaseName, string? releaseGroup, long mediaAssetId,
            string rootRelativePath, long storageMountId, MediaType mediaTypeId, decimal? unitNumber, decimal? parentUnitNumber,
            string? imdbId, string? tmdbId, string[] trackLanguages, string[] siblingPaths)
        {
            Id = id;
            LibraryRootId = libraryRootId;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            RelativePath = relativePath;
            ReleaseName = releaseName;
            ReleaseGroup = releaseGroup;
            MediaAssetId = mediaAssetId;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
            MediaTypeId = mediaTypeId;
            UnitNumber = unitNumber;
            ParentUnitNumber = parentUnitNumber;
            ImdbId = imdbId;
            TmdbId = tmdbId;
            TrackLanguages = trackLanguages;
            SiblingPaths = siblingPaths;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title), false)]
        public string? ReleaseName { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ReleaseGroup))]
        public string? ReleaseGroup { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootRelativePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? UnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? ParentUnitNumber { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value), false)]
        public string? ImdbId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value), false)]
        public string? TmdbId { get; }

        // Embedded subtitle streams as their files name them.
        [DataField(typeof(SubtitleFields), nameof(SubtitleFields.TrackLanguages))]
        public string[] TrackLanguages { get; }

        // Files beside the video that extend its name.
        [DataField(typeof(SubtitleFields), nameof(SubtitleFields.SiblingPaths))]
        public string[] SiblingPaths { get; }
    }

    // The video due for a subtitle check, or the chosen one.
    internal sealed class SubtitleSourceParameters : BaseData
    {
        public SubtitleSourceParameters(long? storedFileId)
        {
            StoredFileId = storedFileId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id), false)]
        public long? StoredFileId { get; }
    }

    internal sealed class SubtitleCheckData : BaseData
    {
        public SubtitleCheckData(long id, long sizeBytes, DateTime modifiedAt)
        {
            Id = id;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }
    }

    internal sealed class SubtitleFileData : BaseData
    {
        public SubtitleFileData(long libraryRootId, string relativePath, long sizeBytes, DateTime modifiedAt, long mediaAssetId)
        {
            LibraryRootId = libraryRootId;
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            MediaAssetId = mediaAssetId;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }
    }

    internal sealed class SubtitleLanguagesData : BaseData
    {
        public SubtitleLanguagesData(string[] subtitleLanguages)
        {
            SubtitleLanguages = subtitleLanguages;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.SubtitleLanguages))]
        public string[] SubtitleLanguages { get; }
    }

    // Albums by their MusicBrainz release groups.
    internal sealed class MusicAlbumLookupData : BaseData
    {
        public MusicAlbumLookupData(string[] externalIds)
        {
            ExternalIds = externalIds;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalIds))]
        public string[] ExternalIds { get; }
    }

    internal sealed class MusicAlbumMatchData : BaseData
    {
        public MusicAlbumMatchData(string externalId, long workId, int trackCount)
        {
            ExternalId = externalId;
            WorkId = workId;
            TrackCount = trackCount;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId))]
        public string ExternalId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.TrackCount))]
        public int TrackCount { get; }
    }
}
