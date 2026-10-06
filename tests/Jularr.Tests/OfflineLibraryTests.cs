using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.OfflineLibrary;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// Server-side offline library contract (#221 part 1): manifest/chapter
/// hashing and differential detection, asset path safety, and sync
/// idempotency/conflict rules (progress forward-only, bookmark
/// last-writer-wins with tombstones), all profile-isolated.
/// </summary>
[TestClass]
public sealed class OfflineLibraryTests
{
    [TestMethod]
    public void ChapterHashChangesWhenSourceOrTranslationsChangeAndIsOrderIndependent()
    {
        var baseline = OfflineLibraryContract.ComputeChapterHash("source-1", []);
        var sameAgain = OfflineLibraryContract.ComputeChapterHash("source-1", []);
        var editedSource = OfflineLibraryContract.ComputeChapterHash("source-2", []);

        Assert.AreEqual(baseline, sameAgain, "Hashing must be deterministic.");
        Assert.AreNotEqual(baseline, editedSource, "A changed source hash must change the chapter version.");

        var withTranslation = OfflineLibraryContract.ComputeChapterHash(
            "source-1",
            [("de", "provider-a", 1, "source-1")]);
        Assert.AreNotEqual(baseline, withTranslation, "Adding a translation must change the chapter version.");

        var reorderedTranslations = OfflineLibraryContract.ComputeChapterHash(
            "source-1",
            [("de", "provider-a", 1, "source-1"), ("ja", "provider-b", 2, "source-1")]);
        var sameSetDifferentOrder = OfflineLibraryContract.ComputeChapterHash(
            "source-1",
            [("ja", "provider-b", 2, "source-1"), ("de", "provider-a", 1, "source-1")]);
        Assert.AreEqual(
            reorderedTranslations,
            sameSetDifferentOrder,
            "The same set of translations must hash the same regardless of input order.");

        var newerPromptVersion = OfflineLibraryContract.ComputeChapterHash(
            "source-1",
            [("de", "provider-a", 2, "source-1")]);
        Assert.AreNotEqual(withTranslation, newerPromptVersion, "A new prompt version must change the chapter version.");
    }

    [TestMethod]
    public void WorkContentVersionChangesWhenAnyChapterHashOrMetadataChangesAndOrderMatters()
    {
        var baseline = OfflineLibraryContract.ComputeWorkContentVersion(
            "Title", "Author", "cover", ["h1", "h2", "h3"]);
        var sameAgain = OfflineLibraryContract.ComputeWorkContentVersion(
            "Title", "Author", "cover", ["h1", "h2", "h3"]);
        Assert.AreEqual(baseline, sameAgain);

        var oneChapterChanged = OfflineLibraryContract.ComputeWorkContentVersion(
            "Title", "Author", "cover", ["h1", "h2-edited", "h3"]);
        Assert.AreNotEqual(baseline, oneChapterChanged, "An edited chapter must change the work version.");

        var reordered = OfflineLibraryContract.ComputeWorkContentVersion(
            "Title", "Author", "cover", ["h2", "h1", "h3"]);
        Assert.AreNotEqual(baseline, reordered, "Chapter reading order is part of the work version.");

        var chapterAdded = OfflineLibraryContract.ComputeWorkContentVersion(
            "Title", "Author", "cover", ["h1", "h2", "h3", "h4"]);
        Assert.AreNotEqual(baseline, chapterAdded, "A new chapter must change the work version.");

        var metadataChanged = OfflineLibraryContract.ComputeWorkContentVersion(
            "Title", "Author", "new-cover", ["h1", "h2", "h3"]);
        Assert.AreNotEqual(baseline, metadataChanged, "Cached metadata (cover) is part of the work version.");
    }

    [TestMethod]
    public async Task ManifestListsChaptersInOrderAndDetectsDifferentialChangesByHash()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var assets = new NovelVolumeAssetStore(new DirectoryInfo(TempAssetDirectory()));
            var queries = new OfflineLibraryQueries(db, assets);

            var work = NewWork("Manifest work");
            db.NovelWorks.Add(work);
            var volume = NewVolume(work.Id, 1);
            db.NovelVolumes.Add(volume);
            var chapter1 = NewChapter(work.Id, volume.Id, 1, "hash-1");
            var chapter2 = NewChapter(work.Id, volume.Id, 2, "hash-2");
            db.NovelChapters.AddRange(chapter1, chapter2);
            await db.SaveChangesAsync();

            var manifest = await queries.GetManifestAsync(work.Id, CancellationToken.None);
            Assert.IsNotNull(manifest);
            Assert.AreEqual(OfflineLibraryContract.SchemaVersion, manifest!.SchemaVersion);
            Assert.AreEqual(2, manifest.Chapters.Count);
            Assert.AreEqual(1, manifest.Chapters[0].Number);
            Assert.AreEqual(2, manifest.Chapters[1].Number);

            var originalVersion = manifest.ContentVersion;
            var originalChapter2Hash = manifest.Chapters[1].Hash;

            // Editing chapter 2's source text (simulated by its source hash changing)
            // must change only that chapter's hash and the whole work's version, not
            // chapter 1's hash: a client compares per-chapter hashes and only
            // re-downloads what changed.
            chapter2.SourceHash = "hash-2-edited";
            await db.SaveChangesAsync();

            var updated = await queries.GetManifestAsync(work.Id, CancellationToken.None);
            Assert.IsNotNull(updated);
            Assert.AreEqual(manifest.Chapters[0].Hash, updated!.Chapters[0].Hash, "Untouched chapter must keep its hash.");
            Assert.AreNotEqual(originalChapter2Hash, updated.Chapters[1].Hash, "Edited chapter must get a new hash.");
            Assert.AreNotEqual(originalVersion, updated.ContentVersion, "The work version must change too.");
        }
        finally
        {
            Cleanup(path);
        }
    }

    [TestMethod]
    public async Task ChapterPayloadIncludesOnlyCurrentTranslationsAndResolvesImageAssetUrls()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var assetRoot = new DirectoryInfo(TempAssetDirectory());
            var assets = new NovelVolumeAssetStore(assetRoot);
            var queries = new OfflineLibraryQueries(db, assets);

            var work = NewWork("Payload work");
            db.NovelWorks.Add(work);
            var volume = NewVolume(work.Id, 1);
            db.NovelVolumes.Add(volume);

            var assetBytes = "not-really-an-image"u8.ToArray();
            var assetName = await assets.SaveAsync(volume.Id, assetBytes, "image/png", CancellationToken.None);
            Assert.IsNotNull(assetName);

            var blocks = NovelChapterDocument.Serialize(
            [
                new NovelContentBlock(NovelContentBlock.ParagraphKind, [new NovelInlineRun("Hello.")]),
                new NovelContentBlock(NovelContentBlock.ImageKind, Source: assetName, Alt: "An illustration")
            ]);

            var chapter = NewChapter(work.Id, volume.Id, 1, "hash-1");
            chapter.OriginalText = "Hello.";
            chapter.ContentJson = blocks;
            db.NovelChapters.Add(chapter);

            db.NovelTranslations.Add(new NovelTranslation
            {
                ChapterId = chapter.Id,
                TargetLanguage = "de",
                ProviderId = "test",
                PromptVersion = 1,
                SourceHash = chapter.SourceHash,
                Text = "Hallo.",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5)
            });
            // A stale translation left over from before the chapter was edited
            // (source hash no longer matches) must not appear in the payload.
            db.NovelTranslations.Add(new NovelTranslation
            {
                ChapterId = chapter.Id,
                TargetLanguage = "fr",
                ProviderId = "test",
                PromptVersion = 1,
                SourceHash = "stale-hash",
                Text = "Bonjour.",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var payload = await queries.GetChapterPayloadAsync(chapter.Id, CancellationToken.None);
            Assert.IsNotNull(payload);
            Assert.AreEqual(1, payload!.Translations.Count);
            Assert.AreEqual("de", payload.Translations[0].TargetLanguage);
            Assert.AreEqual("Hallo.", payload.Translations[0].Text);

            var imageBlock = payload.Blocks.Single(x => x.Kind == NovelContentBlock.ImageKind);
            Assert.AreEqual(NovelVolumeAssetStore.Url(volume.Id, assetName!), imageBlock.ImageAssetUrl);
            Assert.AreEqual("An illustration", imageBlock.ImageAlt);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [TestMethod]
    public async Task AssetPathResolutionRejectsTraversalAndUnknownNamesButAcceptsSavedAssets()
    {
        var root = new DirectoryInfo(TempAssetDirectory());
        var assets = new NovelVolumeAssetStore(root);
        var volumeId = Guid.NewGuid();

        Assert.IsNull(assets.Resolve(volumeId, "../../secrets.txt"));
        Assert.IsNull(assets.Resolve(volumeId, "..%2f..%2fsecrets.txt"));
        Assert.IsNull(assets.Resolve(volumeId, "not-a-hash.png"));
        Assert.IsNull(assets.Resolve(volumeId, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.exe"));
        Assert.IsNull(assets.Resolve(volumeId, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png"), "Never saved, must not resolve.");
        Assert.IsFalse(NovelVolumeAssetStore.IsAssetName("../evil.png"));

        var saved = await assets.SaveAsync(volumeId, "hello"u8.ToArray(), "image/png", CancellationToken.None);
        Assert.IsNotNull(saved);
        Assert.IsTrue(NovelVolumeAssetStore.IsAssetName(saved));
        Assert.IsNotNull(assets.Resolve(volumeId, saved));

        // A different volume id must not see another volume's assets.
        Assert.IsNull(assets.Resolve(Guid.NewGuid(), saved));
    }

    [TestMethod]
    public void ProgressDecisionIsForwardOnlyAcrossChaptersAndWithinAChapter()
    {
        Assert.AreEqual(
            OfflineLibraryProgressOutcome.Applied,
            OfflineLibrarySyncRules.DecideProgress(null, (1, 100)),
            "No prior progress: always applied.");
        Assert.AreEqual(
            OfflineLibraryProgressOutcome.Applied,
            OfflineLibrarySyncRules.DecideProgress((1, 100), (1, 200)),
            "Later position in the same chapter.");
        Assert.AreEqual(
            OfflineLibraryProgressOutcome.Applied,
            OfflineLibrarySyncRules.DecideProgress((1, 900), (2, 0)),
            "A later chapter always counts as forward, even at position 0.");
        Assert.AreEqual(
            OfflineLibraryProgressOutcome.Unchanged,
            OfflineLibrarySyncRules.DecideProgress((1, 100), (1, 100)),
            "An exact replay is a no-op.");
        Assert.AreEqual(
            OfflineLibraryProgressOutcome.IgnoredBehind,
            OfflineLibrarySyncRules.DecideProgress((2, 0), (1, 900)),
            "An earlier chapter never rewinds progress.");
        Assert.AreEqual(
            OfflineLibraryProgressOutcome.IgnoredBehind,
            OfflineLibrarySyncRules.DecideProgress((1, 200), (1, 100)),
            "An earlier position in the same chapter never rewinds progress.");
    }

    [TestMethod]
    public void BookmarkDecisionIsLastWriterWinsWithTombstonesAndIdempotentReplay()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);

        Assert.AreEqual(
            OfflineBookmarkOutcome.Applied,
            OfflineLibrarySyncRules.DecideBookmark(null, null, OfflineBookmarkEventType.Upsert, t0),
            "A brand new bookmark is applied.");

        Assert.AreEqual(
            OfflineBookmarkOutcome.Unchanged,
            OfflineLibrarySyncRules.DecideBookmark(t0, null, OfflineBookmarkEventType.Upsert, t0),
            "Replaying the exact same upsert event is a no-op.");

        Assert.AreEqual(
            OfflineBookmarkOutcome.Applied,
            OfflineLibrarySyncRules.DecideBookmark(t0, null, OfflineBookmarkEventType.Upsert, t1),
            "A newer edit is applied.");

        Assert.AreEqual(
            OfflineBookmarkOutcome.IgnoredStale,
            OfflineLibrarySyncRules.DecideBookmark(t1, null, OfflineBookmarkEventType.Upsert, t0),
            "An edit older than the stored one never wins.");

        Assert.AreEqual(
            OfflineBookmarkOutcome.Removed,
            OfflineLibrarySyncRules.DecideBookmark(t1, null, OfflineBookmarkEventType.Remove, t2),
            "A newer remove wins over an existing bookmark.");

        Assert.AreEqual(
            OfflineBookmarkOutcome.IgnoredStale,
            OfflineLibrarySyncRules.DecideBookmark(null, t2, OfflineBookmarkEventType.Upsert, t1),
            "A stale add replayed after a newer tombstone must never resurrect the bookmark.");

        Assert.AreEqual(
            OfflineBookmarkOutcome.Unchanged,
            OfflineLibrarySyncRules.DecideBookmark(null, t2, OfflineBookmarkEventType.Remove, t2),
            "Replaying the exact same remove event is a no-op.");

        Assert.AreEqual(
            OfflineBookmarkOutcome.Applied,
            OfflineLibrarySyncRules.DecideBookmark(null, t1, OfflineBookmarkEventType.Upsert, t2),
            "A newer add after a remove resurrects the bookmark.");

        Assert.AreEqual(
            OfflineBookmarkOutcome.IgnoredStale,
            OfflineLibrarySyncRules.DecideBookmark(null, t1, OfflineBookmarkEventType.Remove, t0),
            "An older remove than the existing tombstone never wins.");
    }

    [TestMethod]
    public async Task ProgressReconciliationIsIdempotentAndProfileIsolated()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var progressService = new NovelProgressService(db);
            var reconciler = new OfflineLibraryProgressReconciler(db, progressService);

            var work = NewWork("Progress work");
            db.NovelWorks.Add(work);
            var volume = NewVolume(work.Id, 1);
            db.NovelVolumes.Add(volume);
            var chapter1 = NewChapter(work.Id, volume.Id, 1, "hash-1");
            chapter1.OriginalText = "Paragraph one.\n\nParagraph two.";
            var chapter2 = NewChapter(work.Id, volume.Id, 2, "hash-2");
            chapter2.OriginalText = "Another paragraph.";
            db.NovelChapters.AddRange(chapter1, chapter2);
            await db.SaveChangesAsync();

            var forward = new OfflineLibraryProgressCheckpoint(
                Guid.NewGuid(), work.Id, chapter1.Id, 400, "ja", 0, 0, DateTime.UtcNow);

            var first = await reconciler.ReconcileAsync("profile-a", [forward], CancellationToken.None);
            Assert.AreEqual(OfflineLibraryProgressOutcome.Applied, first[0].Outcome);
            Assert.AreEqual(400, first[0].Progress!.PositionPermille);

            var replay = await reconciler.ReconcileAsync("profile-a", [forward], CancellationToken.None);
            Assert.AreEqual(OfflineLibraryProgressOutcome.Unchanged, replay[0].Outcome);

            var behind = forward with { ClientEventId = Guid.NewGuid(), PositionPermille = 100 };
            var behindResult = await reconciler.ReconcileAsync("profile-a", [behind], CancellationToken.None);
            Assert.AreEqual(OfflineLibraryProgressOutcome.IgnoredBehind, behindResult[0].Outcome);

            var laterChapter = new OfflineLibraryProgressCheckpoint(
                Guid.NewGuid(), work.Id, chapter2.Id, 0, "ja", 0, 0, DateTime.UtcNow);
            var laterChapterResult = await reconciler.ReconcileAsync("profile-a", [laterChapter], CancellationToken.None);
            Assert.AreEqual(OfflineLibraryProgressOutcome.Applied, laterChapterResult[0].Outcome);

            var missingChapter = forward with { ClientEventId = Guid.NewGuid(), ChapterId = Guid.NewGuid() };
            var missingResult = await reconciler.ReconcileAsync("profile-a", [missingChapter], CancellationToken.None);
            Assert.AreEqual(OfflineLibraryProgressOutcome.ChapterNotFound, missingResult[0].Outcome);

            // Profile isolation: a second profile has never synced, so the same
            // checkpoint that is now "behind" for profile-a is brand new for profile-b.
            var otherProfile = await reconciler.ReconcileAsync("profile-b", [forward with { ClientEventId = Guid.NewGuid() }], CancellationToken.None);
            Assert.AreEqual(OfflineLibraryProgressOutcome.Applied, otherProfile[0].Outcome);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [TestMethod]
    public async Task BookmarkReconciliationIsIdempotentTombstonedAndProfileIsolated()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var reconciler = new OfflineLibraryBookmarkReconciler(db);

            var work = NewWork("Bookmark work");
            db.NovelWorks.Add(work);
            var volume = NewVolume(work.Id, 1);
            db.NovelVolumes.Add(volume);
            var chapter = NewChapter(work.Id, volume.Id, 1, "hash-1");
            chapter.OriginalText = "Paragraph one.\n\nParagraph two.";
            db.NovelChapters.Add(chapter);
            await db.SaveChangesAsync();

            var bookmarkId = Guid.NewGuid();
            var t0 = DateTime.UtcNow;
            var add = new OfflineBookmarkEvent(
                Guid.NewGuid(), bookmarkId, OfflineBookmarkEventType.Upsert,
                work.Id, chapter.Id, "ja", 100, 0, 0, null, "First read", null, null, t0);

            var applied = await reconciler.ReconcileAsync("profile-a", [add], CancellationToken.None);
            Assert.AreEqual(OfflineBookmarkOutcome.Applied, applied[0].Outcome);
            Assert.AreEqual(1, await db.NovelBookmarks.CountAsync());

            var replay = await reconciler.ReconcileAsync("profile-a", [add], CancellationToken.None);
            Assert.AreEqual(OfflineBookmarkOutcome.Unchanged, replay[0].Outcome);

            var staleEdit = add with { ClientEventId = Guid.NewGuid(), ClientTimestampUtc = t0.AddSeconds(-30), Label = "Stale" };
            var staleResult = await reconciler.ReconcileAsync("profile-a", [staleEdit], CancellationToken.None);
            Assert.AreEqual(OfflineBookmarkOutcome.IgnoredStale, staleResult[0].Outcome);
            Assert.AreEqual("First read", (await db.NovelBookmarks.SingleAsync()).Label);

            var remove = new OfflineBookmarkEvent(
                Guid.NewGuid(), bookmarkId, OfflineBookmarkEventType.Remove,
                work.Id, chapter.Id, "ja", 100, 0, 0, null, null, null, null, t0.AddMinutes(1));
            var removed = await reconciler.ReconcileAsync("profile-a", [remove], CancellationToken.None);
            Assert.AreEqual(OfflineBookmarkOutcome.Removed, removed[0].Outcome);
            Assert.AreEqual(0, await db.NovelBookmarks.CountAsync());
            Assert.AreEqual(1, await db.NovelBookmarkTombstones.CountAsync());

            var staleResurrection = add with { ClientEventId = Guid.NewGuid(), ClientTimestampUtc = t0.AddSeconds(10) };
            var staleResurrectionResult = await reconciler.ReconcileAsync("profile-a", [staleResurrection], CancellationToken.None);
            Assert.AreEqual(
                OfflineBookmarkOutcome.IgnoredStale,
                staleResurrectionResult[0].Outcome,
                "An add older than the removal must never resurrect the bookmark.");
            Assert.AreEqual(0, await db.NovelBookmarks.CountAsync());

            var resurrection = add with { ClientEventId = Guid.NewGuid(), ClientTimestampUtc = t0.AddMinutes(2), Label = "Back again" };
            var resurrectionResult = await reconciler.ReconcileAsync("profile-a", [resurrection], CancellationToken.None);
            Assert.AreEqual(OfflineBookmarkOutcome.Applied, resurrectionResult[0].Outcome);
            Assert.AreEqual(1, await db.NovelBookmarks.CountAsync());
            Assert.AreEqual(0, await db.NovelBookmarkTombstones.CountAsync(), "Resurrection must clear the tombstone.");

            var missingChapter = add with { ClientEventId = Guid.NewGuid(), BookmarkId = Guid.NewGuid(), ChapterId = Guid.NewGuid() };
            var missingResult = await reconciler.ReconcileAsync("profile-a", [missingChapter], CancellationToken.None);
            Assert.AreEqual(OfflineBookmarkOutcome.ChapterNotFound, missingResult[0].Outcome);

            // Profile isolation: profile-b removing the same bookmark id it never
            // owned must not touch profile-a's bookmark or create a shared tombstone.
            var otherProfileRemove = await reconciler.ReconcileAsync(
                "profile-b", [remove with { ClientEventId = Guid.NewGuid(), ClientTimestampUtc = t0.AddMinutes(3) }], CancellationToken.None);
            Assert.AreEqual(OfflineBookmarkOutcome.Removed, otherProfileRemove[0].Outcome);
            Assert.AreEqual(1, await db.NovelBookmarks.CountAsync(x => x.ProfileId == "profile-a"));
            Assert.AreEqual(1, await db.NovelBookmarkTombstones.CountAsync(x => x.ProfileId == "profile-b"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// #374 regression: a Book's arbitrary <see cref="BookLanguageCatalog"/>
    /// target language must survive an offline sync round trip unchanged,
    /// not get hard-normalized to "ja"/"de" the way a Novel's does.
    /// </summary>
    [TestMethod]
    [DataRow("id")]
    [DataRow("en")]
    public async Task BookBookmarkSurvivesSyncRoundTripWithArbitraryLanguageUnchanged(string language)
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var reconciler = new OfflineLibraryBookmarkReconciler(db);

            var work = NewBookWork("Book bookmark work");
            db.NovelWorks.Add(work);
            var volume = NewVolume(work.Id, 1);
            db.NovelVolumes.Add(volume);
            var chapter = NewChapter(work.Id, volume.Id, 1, "hash-1");
            chapter.OriginalText = "Paragraph one.\n\nParagraph two.";
            db.NovelChapters.Add(chapter);
            await db.SaveChangesAsync();

            var add = new OfflineBookmarkEvent(
                Guid.NewGuid(), Guid.NewGuid(), OfflineBookmarkEventType.Upsert,
                work.Id, chapter.Id, language, 250, 3, 12, "Some anchor text.", "My bookmark", "fabric", "#abcdef",
                DateTime.UtcNow);

            var result = await reconciler.ReconcileAsync("profile-a", [add], CancellationToken.None);
            Assert.AreEqual(OfflineBookmarkOutcome.Applied, result[0].Outcome);

            var saved = await db.NovelBookmarks.SingleAsync();
            Assert.AreEqual(language, saved.Language, "A Book's arbitrary target language must not be rewritten to ja/de.");
            Assert.AreEqual(250, saved.PositionPermille);
            Assert.AreEqual(3, saved.ParagraphIndex, "A Book's client-supplied paragraph anchor is not re-resolved server-side.");
            Assert.AreEqual(12, saved.CharacterOffset);
            Assert.AreEqual("Some anchor text.", saved.AnchorText);
            Assert.AreEqual("My bookmark", saved.Label);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [TestMethod]
    [DataRow("id")]
    [DataRow("en")]
    public async Task BookProgressSurvivesSyncRoundTripWithArbitraryLanguageUnchanged(string language)
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var progressService = new NovelProgressService(db);
            var reconciler = new OfflineLibraryProgressReconciler(db, progressService);

            var work = NewBookWork("Book progress work");
            db.NovelWorks.Add(work);
            var volume = NewVolume(work.Id, 1);
            db.NovelVolumes.Add(volume);
            var chapter = NewChapter(work.Id, volume.Id, 1, "hash-1");
            chapter.OriginalText = "Paragraph one.\n\nParagraph two.";
            db.NovelChapters.Add(chapter);
            await db.SaveChangesAsync();

            var checkpoint = new OfflineLibraryProgressCheckpoint(
                Guid.NewGuid(), work.Id, chapter.Id, 400, language, 2, 15, DateTime.UtcNow);

            var result = await reconciler.ReconcileAsync("profile-a", [checkpoint], CancellationToken.None);
            Assert.AreEqual(OfflineLibraryProgressOutcome.Applied, result[0].Outcome);

            var saved = result[0].Progress;
            Assert.IsNotNull(saved);
            Assert.AreEqual(language, saved!.AnchorLanguage, "A Book's arbitrary target language must not be rewritten to ja/de.");
            Assert.AreEqual(400, saved.PositionPermille);
            Assert.AreEqual(2, saved.AnchorParagraphIndex, "A Book's client-supplied paragraph anchor is not re-resolved server-side.");
            Assert.AreEqual(15, saved.AnchorOffset);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// #374 regression guard: Novel-typed works must keep the pre-existing
    /// ja/de-only anchor language behavior after teaching the reconcilers to
    /// special-case Books.
    /// </summary>
    [TestMethod]
    public async Task NovelBookmarkAndProgressStillNormalizeUnsupportedLanguagesToJapanese()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var progressService = new NovelProgressService(db);
            var progressReconciler = new OfflineLibraryProgressReconciler(db, progressService);
            var bookmarkReconciler = new OfflineLibraryBookmarkReconciler(db);

            var work = NewWork("Novel work");
            db.NovelWorks.Add(work);
            var volume = NewVolume(work.Id, 1);
            db.NovelVolumes.Add(volume);
            var chapter = NewChapter(work.Id, volume.Id, 1, "hash-1");
            chapter.OriginalText = "Paragraph one.\n\nParagraph two.";
            db.NovelChapters.Add(chapter);
            await db.SaveChangesAsync();

            var checkpoint = new OfflineLibraryProgressCheckpoint(
                Guid.NewGuid(), work.Id, chapter.Id, 400, "en", 0, 0, DateTime.UtcNow);
            var progressResult = await progressReconciler.ReconcileAsync("profile-a", [checkpoint], CancellationToken.None);
            Assert.AreEqual(
                NovelReadingLanguage.Japanese,
                progressResult[0].Progress!.AnchorLanguage,
                "A Novel's anchor language must still hard-normalize anything but German to Japanese.");

            var add = new OfflineBookmarkEvent(
                Guid.NewGuid(), Guid.NewGuid(), OfflineBookmarkEventType.Upsert,
                work.Id, chapter.Id, "en", 100, 0, 0, null, null, null, null, DateTime.UtcNow);
            var bookmarkResult = await bookmarkReconciler.ReconcileAsync("profile-a", [add], CancellationToken.None);
            Assert.AreEqual(OfflineBookmarkOutcome.Applied, bookmarkResult[0].Outcome);
            var savedBookmark = await db.NovelBookmarks.SingleAsync();
            Assert.AreEqual(
                NovelReadingLanguage.Japanese,
                savedBookmark.Language,
                "A Novel's bookmark language must still hard-normalize anything but German to Japanese.");

            var germanCheckpoint = checkpoint with { ClientEventId = Guid.NewGuid(), AnchorLanguage = "de", PositionPermille = 500 };
            var germanResult = await progressReconciler.ReconcileAsync("profile-a", [germanCheckpoint], CancellationToken.None);
            Assert.AreEqual(NovelReadingLanguage.German, germanResult[0].Progress!.AnchorLanguage);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static NovelWork NewWork(string title, string sourceProvider = "test") => new()
    {
        SourceProvider = sourceProvider,
        SourceKey = Guid.NewGuid().ToString("N"),
        SourceUrl = "https://example.invalid/work",
        Title = title
    };

    private static NovelWork NewBookWork(string title) =>
        NewWork(title, BookCatalogService.ImportedBookProvider);

    private static NovelVolume NewVolume(Guid workId, int number) => new()
    {
        WorkId = workId,
        Number = number,
        Kind = NovelVolumeKinds.Web,
        SourceKey = Guid.NewGuid().ToString("N")
    };

    private static NovelChapter NewChapter(Guid workId, Guid volumeId, int number, string sourceHash) => new()
    {
        WorkId = workId,
        VolumeId = volumeId,
        Number = number,
        Title = $"Chapter {number}",
        OriginalText = $"Text of chapter {number}.",
        SourceHash = sourceHash,
        SourceUrl = $"https://example.invalid/{number}"
    };

    private static string TempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"jularr-offline-library-{Guid.NewGuid():N}.db");

    private static string TempAssetDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jularr-offline-library-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;
        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    private static void Cleanup(string path)
    {
        File.Delete(path);
    }
}
