using Jularr.Web.Data;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Games;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

[TestClass]
public sealed class OfflinePackageOptionsTests
{
    [TestMethod]
    public async Task MovieOptionsUseCanonicalVideoAssetAndRemainReadOnly()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();

        var work = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Offline Movie" };
        var version = new WorkVersion { WorkId = work.Id, VersionKey = "movie-source", Quality = "1080p" };
        var asset = new MediaAsset { WorkId = work.Id, WorkVersionId = version.Id, Kind = MediaAssetKind.Video };
        var root = new LibraryRoot { Name = "Movies", Path = "/library/movies" };
        var file = new StoredFile
        {
            MediaAssetId = asset.Id,
            LibraryRootId = root.Id,
            Path = "/library/movies/offline-movie.mkv",
            SizeBytes = 1_234,
            LastWriteTimeUtc = DateTime.UtcNow
        };

        db.AddRange(work, version, asset, root, file);
        db.MediaTracks.AddRange(
            new MediaTrack { MediaFileId = file.Id, StreamIndex = 1, Kind = MediaTrackKind.Audio, Language = "ja", IsDefault = true },
            new MediaTrack { MediaFileId = file.Id, StreamIndex = 2, Kind = MediaTrackKind.Subtitle, Language = "de" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new ClientApiOfflinePackageOptionsService(db, NewInstanceModules());
        var target = new ClientOfflinePackageTarget(ClientApiOfflinePackageContract.WorkTarget, WorkId: work.Id);
        var result = await service.GetOptionsAsync(target, ClientApiOfflinePackageContract.WatchIntent, CancellationToken.None);

        Assert.AreEqual(ClientOfflinePackageQueryStatus.Success, result.Status);
        Assert.IsNotNull(result.Value);
        Assert.IsTrue(result.Value!.Downloadable);
        Assert.AreEqual(ClientApiOfflinePackageContract.VideoCategory, result.Value.Category);
        Assert.AreEqual(ClientApiOfflinePackageContract.KnownEstimate, result.Value.Estimate.Status);
        Assert.AreEqual(1_234L, result.Value.Estimate.Bytes);
        Assert.AreEqual("1080p", result.Value.Qualities.Single().Key);
        Assert.AreEqual("ja", result.Value.AudioTracks.Single().Language);
        Assert.AreEqual("de", result.Value.SubtitleTracks.Single().Language);
        Assert.IsFalse(db.ChangeTracker.HasChanges(), "Options must not mutate canonical media or local package state.");
    }

    [TestMethod]
    public async Task GameOptionsExposeCanonicalReleaseAndExactPreviewSize()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();

        var game = new Game { CanonicalTitle = "Offline RPG" };
        var platform = new GamePlatform { Key = "psx", DisplayName = "PlayStation" };
        var root = new LibraryRoot { Name = "Games", Path = "/library/games" };
        var release = new GameRelease { GameId = game.Id, GamePlatformId = platform.Id, Region = "US", Version = "1.0" };

        db.AddRange(game, platform, root, release);
        db.GameReleaseFiles.AddRange(
            new GameReleaseFile
            {
                GameReleaseId = release.Id,
                LibraryRootId = root.Id,
                RelativePath = "Offline RPG/disc-1.bin",
                SizeBytes = 1_024,
                Sequence = 0,
                Role = GameReleaseFileRole.Disc
            },
            new GameReleaseFile
            {
                GameReleaseId = release.Id,
                LibraryRootId = root.Id,
                RelativePath = "Offline RPG/disc-2.bin",
                SizeBytes = 2_048,
                Sequence = 1,
                Role = GameReleaseFileRole.Disc
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new ClientApiOfflinePackageOptionsService(db, NewInstanceModules());
        var target = new ClientOfflinePackageTarget(ClientApiOfflinePackageContract.GameTarget, GameId: game.Id);
        var options = await service.GetOptionsAsync(target, ClientApiOfflinePackageContract.PlayIntent, CancellationToken.None);

        Assert.AreEqual(ClientOfflinePackageQueryStatus.Success, options.Status);
        Assert.IsNotNull(options.Value);
        Assert.AreEqual(ClientApiOfflinePackageContract.GameCategory, options.Value!.Category);
        Assert.IsTrue(options.Value.Downloadable);
        Assert.AreEqual(release.Id, options.Value.Units.Single().Id);
        Assert.AreEqual(ClientApiOfflinePackageContract.KnownEstimate, options.Value.Estimate.Status);
        Assert.AreEqual(3_072L, options.Value.Estimate.Bytes);

        var preview = await service.PreviewAsync(
            target,
            ClientApiOfflinePackageContract.PlayIntent,
            new ClientOfflinePackageSelection(
                Scope: ClientApiOfflinePackageContract.ReleaseScope,
                UnitIds: [release.Id]),
            CancellationToken.None);

        Assert.AreEqual(ClientOfflinePackageQueryStatus.Success, preview.Status);
        Assert.IsNotNull(preview.Value);
        Assert.AreEqual(ClientApiOfflinePackageContract.KnownEstimate, preview.Value!.Estimate.Status);
        Assert.AreEqual(3_072L, preview.Value.Estimate.Bytes);
        Assert.IsFalse(db.ChangeTracker.HasChanges(), "Game package preview must not create install/download state.");
    }

    [TestMethod]
    public async Task WorkTargetRejectsChapterOwnedByAnotherWork()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();

        var requested = new Work { MediaType = WorkMediaType.Book, CanonicalTitle = "Requested" };
        var other = new Work { MediaType = WorkMediaType.Book, CanonicalTitle = "Other" };
        var chapter = new WorkChapter { WorkId = other.Id, Number = 1, Title = "Wrong Work" };
        db.AddRange(requested, other, chapter);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new ClientApiOfflinePackageOptionsService(db, NewInstanceModules());
        var target = new ClientOfflinePackageTarget(
            ClientApiOfflinePackageContract.WorkTarget,
            WorkId: requested.Id,
            WorkChapterId: chapter.Id);

        var result = await service.GetOptionsAsync(target, ClientApiOfflinePackageContract.ReadIntent, CancellationToken.None);

        Assert.AreEqual(ClientOfflinePackageQueryStatus.Invalid, result.Status);
        Assert.AreEqual("invalid_offline_target", result.ErrorCode);
        Assert.IsFalse(db.ChangeTracker.HasChanges());
    }

    [TestMethod]
    public async Task DisabledInstanceModuleBlocksWorkOfflineOptions()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();

        var work = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Disabled Movie" };
        db.Works.Add(work);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var modules = NewInstanceModules();
        await modules.SetAsync(InstanceModule.Movie, enabled: false, cancellationToken: CancellationToken.None);
        var service = new ClientApiOfflinePackageOptionsService(db, modules);
        var target = new ClientOfflinePackageTarget(ClientApiOfflinePackageContract.WorkTarget, WorkId: work.Id);

        var result = await service.GetOptionsAsync(target, ClientApiOfflinePackageContract.WatchIntent, CancellationToken.None);

        Assert.AreEqual(ClientOfflinePackageQueryStatus.NotFound, result.Status);
        Assert.AreEqual("offline_target_not_found", result.ErrorCode);
        Assert.IsFalse(db.ChangeTracker.HasChanges());
    }

    private static InstanceModuleStore NewInstanceModules() =>
        new(Path.Combine(Path.GetTempPath(), $"jularr-offline-options-{Guid.NewGuid():N}"));
}
