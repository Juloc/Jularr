using Jularr.Web.Features.Acquisition.Api;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Ai;
using Jularr.Web.Features.ReaderPreferences;
using Jularr.Web.Features.Audiobooks;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Collections;
using Jularr.Web.Features.Games;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Learning.Courses;
using Jularr.Web.Features.Learning.Curriculum;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Tv;
using Jularr.Web.Features.OfflineLibrary;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Storage.Reconciliation;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Features.Watchlist;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jularr.Web.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // File modification times are compared byte-for-byte against the live file system to detect
    // changes and to relink moved files. PostgreSQL timestamptz only keeps microseconds, which would
    // truncate the file system's 100 ns ticks and make every rescan see a "changed" file. Storing
    // these specific columns as round-trip ISO text preserves full precision (as the SQLite epoch did).
    private static readonly ValueConverter<DateTime, string> FileTimestampConverter = new(
        v => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        v => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime());

    public DbSet<LibraryRoot> LibraryRoots => Set<LibraryRoot>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<GameTitle> GameTitles => Set<GameTitle>();
    public DbSet<GameExternalIdentity> GameExternalIdentities => Set<GameExternalIdentity>();
    public DbSet<GameArtwork> GameArtworks => Set<GameArtwork>();
    public DbSet<GamePlatform> GamePlatforms => Set<GamePlatform>();
    public DbSet<GameRelease> GameReleases => Set<GameRelease>();
    public DbSet<GameReleaseHash> GameReleaseHashes => Set<GameReleaseHash>();
    public DbSet<GameReleaseFile> GameReleaseFiles => Set<GameReleaseFile>();
    public DbSet<Anime> Anime => Set<Anime>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<StoredFile> MediaFiles => StoredFiles;
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<LibraryReconciliationPlan> LibraryReconciliationPlans => Set<LibraryReconciliationPlan>();
    public DbSet<LibraryReconciliationPlanItem> LibraryReconciliationPlanItems => Set<LibraryReconciliationPlanItem>();
    public DbSet<LibraryReconciliationLogicalGroup> LibraryReconciliationLogicalGroups => Set<LibraryReconciliationLogicalGroup>();
    public DbSet<LibraryReconciliationFileLink> LibraryReconciliationFileLinks => Set<LibraryReconciliationFileLink>();
    public DbSet<MediaTechnicalAnalysis> MediaTechnicalAnalyses => Set<MediaTechnicalAnalysis>();
    public DbSet<MediaTechnicalAnalysis> MediaAnalyses => MediaTechnicalAnalyses;
    public DbSet<MediaTrack> MediaTracks => Set<MediaTrack>();
    public DbSet<MediaTrack> MediaAnalysisStreams => MediaTracks;
    public DbSet<AnimeMetadata> AnimeMetadata => Set<AnimeMetadata>();
    public DbSet<AnimeLocalMetadata> AnimeLocalMetadata => Set<AnimeLocalMetadata>();
    public DbSet<SubtitleTrack> SubtitleTracks => Set<SubtitleTrack>();
    public DbSet<SubtitleCue> SubtitleCues => Set<SubtitleCue>();
    public DbSet<SubtitleLanguageProfile> SubtitleLanguageProfiles => Set<SubtitleLanguageProfile>();
    public DbSet<SubtitleLanguageProfileItem> SubtitleLanguageProfileItems => Set<SubtitleLanguageProfileItem>();
    public DbSet<SubtitleProfileAssignment> SubtitleProfileAssignments => Set<SubtitleProfileAssignment>();
    public DbSet<Term> Terms => Set<Term>();
    public DbSet<EpisodeTerm> EpisodeTerms => Set<EpisodeTerm>();
    public DbSet<LearningUnit> LearningUnits => Set<LearningUnit>();
    public DbSet<LearningVariant> LearningVariants => Set<LearningVariant>();
    public DbSet<LearningCourse> LearningCourses => Set<LearningCourse>();
    public DbSet<LearningCard> LearningCards => Set<LearningCard>();
    public DbSet<LearningCardReview> LearningCardReviews => Set<LearningCardReview>();
    public DbSet<LearningContext> LearningContexts => Set<LearningContext>();
    public DbSet<LearningPreferences> LearningPreferences => Set<LearningPreferences>();

    // Learning v3 curriculum foundation (#441): the media-independent blueprint hierarchy
    // (Curriculum → Level → Chapter → Lesson → Exercise), shared (deduped) course instances a
    // language pair specializes, and each learner's personal variant with its own delta and course
    // progress. Card review state stays in the v2 LearningCard/LearningCardReview model above.
    public DbSet<CurriculumBlueprint> CurriculumBlueprints => Set<CurriculumBlueprint>();
    public DbSet<CurriculumLevel> CurriculumLevels => Set<CurriculumLevel>();
    public DbSet<CurriculumChapter> CurriculumChapters => Set<CurriculumChapter>();
    public DbSet<CurriculumLesson> CurriculumLessons => Set<CurriculumLesson>();
    public DbSet<CurriculumExercise> CurriculumExercises => Set<CurriculumExercise>();
    public DbSet<SharedCourseInstance> SharedCourseInstances => Set<SharedCourseInstance>();
    public DbSet<LearnerCourse> LearnerCourses => Set<LearnerCourse>();
    public DbSet<LearnerCourseItemDelta> LearnerCourseItemDeltas => Set<LearnerCourseItemDelta>();
    public DbSet<LearnerCourseProgress> LearnerCourseProgress => Set<LearnerCourseProgress>();
    public DbSet<AiSentenceExplanationCache> AiSentenceExplanationCache => Set<AiSentenceExplanationCache>();
    public DbSet<OwnerAccount> OwnerAccounts => Set<OwnerAccount>();
    public DbSet<EpisodeProgress> EpisodeProgress => Set<EpisodeProgress>();
    public DbSet<EpisodePlaybackHistoryEntry> EpisodePlaybackHistory => Set<EpisodePlaybackHistoryEntry>();
    public DbSet<ProfilePlaybackPreferences> ProfilePlaybackPreferences => Set<ProfilePlaybackPreferences>();
    public DbSet<NovelWork> NovelWorks => Set<NovelWork>();
    public DbSet<BookEdition> BookEditions => Set<BookEdition>();
    public DbSet<BookFile> BookFiles => Set<BookFile>();
    public DbSet<NovelVolume> NovelVolumes => Set<NovelVolume>();
    public DbSet<NovelChapter> NovelChapters => Set<NovelChapter>();
    public DbSet<NovelTranslation> NovelTranslations => Set<NovelTranslation>();
    public DbSet<NovelProgress> NovelProgress => Set<NovelProgress>();
    public DbSet<NovelBookmark> NovelBookmarks => Set<NovelBookmark>();
    public DbSet<NovelHighlight> NovelHighlights => Set<NovelHighlight>();
    public DbSet<NovelBookmarkTombstone> NovelBookmarkTombstones => Set<NovelBookmarkTombstone>();
    public DbSet<NovelAnimeMapping> NovelAnimeMappings => Set<NovelAnimeMapping>();
    public DbSet<ReaderPreference> ReaderPreferences => Set<ReaderPreference>();
    public DbSet<EpisodeMediaSegment> EpisodeMediaSegments => Set<EpisodeMediaSegment>();
    public DbSet<EpisodeSegmentDetectionState> EpisodeSegmentDetectionStates => Set<EpisodeSegmentDetectionState>();
    public DbSet<AcquisitionHistoryEntry> AcquisitionHistory => Set<AcquisitionHistoryEntry>();
    public DbSet<AcquisitionApiKey> AcquisitionApiKeys => Set<AcquisitionApiKey>();

    // First-class video media types (#593 Movie, #594 TV): per-type records bridged to the universal
    // media core through WorkSourceKind.Movie / WorkSourceKind.Series (TV reuses WorkSeason/WorkEpisode).
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<TvSeries> TvSeries => Set<TvSeries>();

    // First-class audiobook media type (#440): per-type audiobook + its audio files, bridged to the
    // universal media core as a Book Work with an "audiobook" WorkEdition/WorkVersion through
    // WorkSourceKind.Audiobook, plus canonical per-profile listening progress.
    public DbSet<Audiobook> Audiobooks => Set<Audiobook>();
    public DbSet<AudiobookFile> AudiobookFiles => Set<AudiobookFile>();
    public DbSet<AudiobookProgress> AudiobookProgress => Set<AudiobookProgress>();

    // Universal media core (#592): provider-independent works, external identities, titles, structure
    // (seasons/episodes, volumes/chapters), editions/versions, typed relations, field-level provenance
    // and the non-invasive bridge to the existing per-type records.
    public DbSet<Work> Works => Set<Work>();
    public DbSet<WorkTitle> WorkTitles => Set<WorkTitle>();
    public DbSet<WorkExternalIdentity> WorkExternalIdentities => Set<WorkExternalIdentity>();
    public DbSet<WorkRelation> WorkRelations => Set<WorkRelation>();
    public DbSet<WorkSeason> WorkSeasons => Set<WorkSeason>();
    public DbSet<WorkEpisode> WorkEpisodes => Set<WorkEpisode>();
    public DbSet<WorkVolume> WorkVolumes => Set<WorkVolume>();
    public DbSet<WorkChapter> WorkChapters => Set<WorkChapter>();
    public DbSet<WorkEdition> WorkEditions => Set<WorkEdition>();
    public DbSet<WorkVersion> WorkVersions => Set<WorkVersion>();
    public DbSet<WorkFieldProvenance> WorkFieldProvenance => Set<WorkFieldProvenance>();
    public DbSet<WorkSourceLink> WorkSourceLinks => Set<WorkSourceLink>();

    // Universal media core workflow (#432): append-only identity-resolution history (merge/split/reassign).
    public DbSet<WorkIdentityChange> WorkIdentityChanges => Set<WorkIdentityChange>();

    // Smart & manual collections (#427): user-curated and rule-driven cross-media shelves over works.
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();

    /// <summary>Configures relational constraints, conversion rules and query indexes for the application model.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OwnerAccount>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(32);
            entity.Property(x => x.UserName).HasMaxLength(80);
            entity.Property(x => x.NormalizedUserName).HasMaxLength(80);
            entity.Property(x => x.PasswordHash).HasMaxLength(1024);
            entity.Property(x => x.Role).HasConversion<int>();
            entity.HasIndex(x => x.NormalizedUserName).IsUnique();
        });

        modelBuilder.Entity<LibraryRoot>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.Path).HasMaxLength(2048);
            entity.Property(x => x.WakeMacAddress).HasMaxLength(32);
            entity.Property(x => x.WakeBroadcastAddress).HasMaxLength(64);
            entity.HasIndex(x => x.Path).IsUnique();
        });

        GamesModelConfiguration.Configure(modelBuilder);

        modelBuilder.Entity<LibraryReconciliationPlan>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.StartFolder).HasMaxLength(2048);
            entity.Property(x => x.Status).HasConversion<int>();
            entity.Property(x => x.OrganizationMode).HasConversion<int>();
            entity.Property(x => x.Failure).HasMaxLength(1000);
            entity.HasOne<LibraryRoot>().WithMany().HasForeignKey(x => x.LibraryRootId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<LibraryReconciliationPlanItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RelativePath).HasMaxLength(2048);
            entity.Property(x => x.State).HasConversion<int>();
            entity.Property(x => x.DetectionSummary).HasMaxLength(1000);
            entity.Property(x => x.Error).HasMaxLength(1000);
            entity.Property(x => x.Language).HasMaxLength(32);
            entity.Property(x => x.AudioLanguage).HasMaxLength(32);
            entity.Property(x => x.SubtitleLanguage).HasMaxLength(32);
            entity.Property(x => x.QualitySource).HasMaxLength(240);
            entity.Property(x => x.ObservedLastWriteTimeUtc).HasConversion(FileTimestampConverter);
            entity.HasOne<LibraryReconciliationPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.AssignedWorkId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WorkEpisode>().WithMany().HasForeignKey(x => x.AssignedWorkEpisodeId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WorkVolume>().WithMany().HasForeignKey(x => x.AssignedWorkVolumeId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WorkChapter>().WithMany().HasForeignKey(x => x.AssignedWorkChapterId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WorkEdition>().WithMany().HasForeignKey(x => x.AssignedWorkEditionId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WorkVersion>().WithMany().HasForeignKey(x => x.AssignedWorkVersionId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<LibraryReconciliationLogicalGroup>().WithMany().HasForeignKey(x => x.LogicalGroupId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(x => new { x.PlanId, x.RelativePath }).IsUnique();
        });

        modelBuilder.Entity<LibraryReconciliationLogicalGroup>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.HasOne<LibraryReconciliationPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(x => new { x.PlanId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<LibraryReconciliationFileLink>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OriginalRelativePath).HasMaxLength(2048);
            entity.Property(x => x.RelativePath).HasMaxLength(2048);
            entity.Property(x => x.Language).HasMaxLength(32);
            entity.Property(x => x.AudioLanguage).HasMaxLength(32);
            entity.Property(x => x.SubtitleLanguage).HasMaxLength(32);
            entity.Property(x => x.QualitySource).HasMaxLength(240);
            entity.HasOne<LibraryReconciliationPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<LibraryReconciliationPlanItem>().WithMany().HasForeignKey(x => x.PlanItemId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<LibraryRoot>().WithMany().HasForeignKey(x => x.LibraryRootId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WorkEpisode>().WithMany().HasForeignKey(x => x.WorkEpisodeId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WorkVolume>().WithMany().HasForeignKey(x => x.WorkVolumeId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WorkChapter>().WithMany().HasForeignKey(x => x.WorkChapterId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WorkEdition>().WithMany().HasForeignKey(x => x.WorkEditionId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WorkVersion>().WithMany().HasForeignKey(x => x.WorkVersionId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(x => x.PlanItemId).IsUnique();
            entity.HasIndex(x => new { x.LibraryRootId, x.RelativePath }).IsUnique();
        });

        modelBuilder.Entity<Anime>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(300);
            entity.Property(x => x.Title).HasMaxLength(300);
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<AnimeMetadata>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Provider).HasMaxLength(80);
            entity.Property(x => x.ExternalId).HasMaxLength(200);
            entity.Property(x => x.PreferredTitle).HasMaxLength(500);
            entity.Property(x => x.RomajiTitle).HasMaxLength(500);
            entity.Property(x => x.EnglishTitle).HasMaxLength(500);
            entity.Property(x => x.NativeTitle).HasMaxLength(500);
            entity.Property(x => x.CoverImageUrl).HasMaxLength(2048);
            entity.Property(x => x.BannerImageUrl).HasMaxLength(2048);
            entity.Property(x => x.Format).HasMaxLength(80);
            entity.Property(x => x.Status).HasMaxLength(80);
            entity.Property(x => x.Season).HasMaxLength(80);
            entity.HasOne<Anime>().WithOne().HasForeignKey<AnimeMetadata>(x => x.AnimeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.AnimeId).IsUnique();
            entity.HasIndex(x => new { x.Provider, x.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<AnimeLocalMetadata>(entity =>
        {
            entity.HasKey(x => x.AnimeId);
            entity.Property(x => x.Source).HasMaxLength(20);
            entity.Property(x => x.OriginalTitle).HasMaxLength(NfoReader.MaxTitleLength);
            entity.Property(x => x.MyAnimeListId).HasMaxLength(10);
            entity.Property(x => x.TvdbId).HasMaxLength(10);
            entity.Property(x => x.TmdbId).HasMaxLength(10);
            entity.Property(x => x.ImdbId).HasMaxLength(12);
            entity.HasOne<Anime>().WithOne().HasForeignKey<AnimeLocalMetadata>(x => x.AnimeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Episode>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.HasOne<Anime>().WithMany().HasForeignKey(x => x.AnimeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.AnimeId, x.SeasonNumber, x.Number }).IsUnique();
        });

        modelBuilder.Entity<MediaAsset>(entity =>
        {
            entity.ToTable("MediaAssets", table =>
                table.HasCheckConstraint("CK_MediaAssets_Kind", "\"Kind\" >= 0 AND \"Kind\" <= 5"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<int>();
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkEpisode>().WithMany().HasForeignKey(x => x.WorkEpisodeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkVersion>().WithMany().HasForeignKey(x => x.WorkVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.WorkVersionId, x.Kind }).IsUnique();
            entity.HasIndex(x => new { x.WorkId, x.WorkEpisodeId, x.Kind });
        });

        modelBuilder.Entity<StoredFile>(entity =>
        {
            entity.ToTable("StoredFiles", table =>
                table.HasCheckConstraint("CK_StoredFiles_SizeBytes", "\"SizeBytes\" >= 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Path).HasMaxLength(2048);
            entity.Property(x => x.LastWriteTimeUtc).HasConversion(FileTimestampConverter);
            entity.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.MediaAssetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LibraryRoot>().WithMany().HasForeignKey(x => x.LibraryRootId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => x.Path).IsUnique();
            entity.HasIndex(x => x.MediaAssetId);
            entity.HasIndex(x => new { x.LibraryRootId, x.EpisodeId });
        });

        modelBuilder.Entity<MediaTechnicalAnalysis>(entity =>
        {
            entity.ToTable("MediaTechnicalAnalyses");
            entity.HasKey(x => x.MediaFileId);
            entity.Property(x => x.MediaFileId).HasColumnName("StoredFileId");
            entity.Property(x => x.Status).HasConversion<int>();
            entity.Property(x => x.SourceFingerprint).HasMaxLength(64);
            entity.Property(x => x.Diagnostic).HasMaxLength(MediaInventoryService.DiagnosticMaxLength);
            entity.Property(x => x.Container).HasMaxLength(120);
            entity.Property(x => x.VideoCodec).HasMaxLength(64);
            entity.Property(x => x.VideoProfile).HasMaxLength(80);
            entity.Property(x => x.PixelFormat).HasMaxLength(40);
            entity.Property(x => x.DynamicRange).HasMaxLength(24);
            entity.Property(x => x.SourceLastWriteTimeUtc).HasConversion(FileTimestampConverter);
            entity.HasOne<StoredFile>().WithOne().HasForeignKey<MediaTechnicalAnalysis>(x => x.MediaFileId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.Status, x.ProbeVersion });
        });

        modelBuilder.Entity<MediaTrack>(entity =>
        {
            entity.ToTable("MediaTracks");
            entity.HasKey(x => new { x.MediaFileId, x.StreamIndex });
            entity.Property(x => x.MediaFileId).HasColumnName("StoredFileId");
            entity.Property(x => x.Kind).HasConversion<int>();
            entity.Property(x => x.Codec).HasMaxLength(64);
            entity.Property(x => x.Language).HasMaxLength(32);
            entity.Property(x => x.Title).HasMaxLength(300);
            entity.Property(x => x.ChannelLayout).HasMaxLength(64);
            entity.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.MediaFileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubtitleTrack>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Path).HasMaxLength(2048);
            entity.Property(x => x.Language).HasMaxLength(16);
            entity.Property(x => x.Format).HasMaxLength(16);
            entity.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.Path).IsUnique();
            entity.HasIndex(x => new { x.EpisodeId, x.Language });
        });

        modelBuilder.Entity<SubtitleCue>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasOne<SubtitleTrack>().WithMany().HasForeignKey(x => x.SubtitleTrackId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.SubtitleTrackId, x.StartMs });
        });

        modelBuilder.Entity<SubtitleLanguageProfile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<SubtitleLanguageProfileItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LanguageTag).HasMaxLength(16);
            entity.HasOne<SubtitleLanguageProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProfileId, x.SortOrder });
        });

        modelBuilder.Entity<SubtitleProfileAssignment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MediaType).HasConversion<int?>();
            entity.HasOne<SubtitleLanguageProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LibraryRoot>().WithMany().HasForeignKey(x => x.LibraryRootId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.MediaType, x.LibraryRootId });
        });

        modelBuilder.Entity<Term>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Language).HasMaxLength(16);
            entity.Property(x => x.Canonical).HasMaxLength(300);
            entity.Property(x => x.Reading).HasMaxLength(300);
            entity.Property(x => x.Meaning).HasMaxLength(1000);
            entity.HasIndex(x => new { x.Language, x.Canonical }).IsUnique();
        });

        modelBuilder.Entity<EpisodeTerm>(entity =>
        {
            entity.HasKey(x => new { x.EpisodeId, x.TermId });
            entity.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Term>().WithMany().HasForeignKey(x => x.TermId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.TermId);
        });

        modelBuilder.Entity<EpisodeProgress>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
            entity.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProfileId, x.EpisodeId }).IsUnique();
            entity.HasIndex(x => new { x.ProfileId, x.UpdatedAt });
        });

        modelBuilder.Entity<EpisodePlaybackHistoryEntry>(entity =>
        {
            entity.ToTable("EpisodePlaybackHistory");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
            entity.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProfileId, x.LastPlayedAt });
        });

        modelBuilder.Entity<ProfilePlaybackPreferences>(entity =>
        {
            entity.HasKey(x => x.ProfileId);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
            entity.Property(x => x.PreferredAudioLanguage).HasMaxLength(16);
            entity.Property(x => x.PreferredSubtitleLanguage).HasMaxLength(16);
            entity.Property(x => x.DefaultPlaybackSpeed).HasDefaultValue(PlaybackPreferenceRules.DefaultSpeed);
        });

        LearningCourseModelConfiguration.Configure(modelBuilder);
        CurriculumModelConfiguration.Configure(modelBuilder);

        modelBuilder.Entity<LearningPreferences>(entity =>
        {
            entity.HasKey(x => x.ProfileId);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
        });

        modelBuilder.Entity<AiSentenceExplanationCache>(entity =>
        {
            entity.HasKey(x => x.CacheKey);
            entity.Property(x => x.CacheKey).HasMaxLength(64);
            entity.Property(x => x.ProviderId).HasMaxLength(80);
        });

        modelBuilder.Entity<NovelWork>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceProvider).HasMaxLength(80);
            entity.Property(x => x.SourceKey).HasMaxLength(80);
            entity.Property(x => x.SourceUrl).HasMaxLength(2048);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.Property(x => x.Author).HasMaxLength(300);
            entity.Property(x => x.MetadataProvider).HasMaxLength(80);
            entity.Property(x => x.MetadataExternalId).HasMaxLength(200);
            entity.Property(x => x.MetadataTitle).HasMaxLength(500);
            entity.Property(x => x.MetadataNativeTitle).HasMaxLength(500);
            entity.Property(x => x.CoverImageUrl).HasMaxLength(2048);
            entity.Property(x => x.BannerImageUrl).HasMaxLength(2048);
            entity.Property(x => x.Format).HasMaxLength(80);
            entity.Property(x => x.MetadataStatus).HasMaxLength(80);
            entity.Property(x => x.MetadataGenresJson).HasColumnType("TEXT");
            entity.HasIndex(x => new { x.SourceProvider, x.SourceKey }).IsUnique();
            entity.HasIndex(x => new { x.MetadataProvider, x.MetadataExternalId }).IsUnique();
        });

        modelBuilder.Entity<BookEdition>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EditionKey).HasMaxLength(120);
            entity.Property(x => x.Language).HasMaxLength(16);
            entity.Property(x => x.Isbn10).HasMaxLength(10);
            entity.Property(x => x.Isbn13).HasMaxLength(13);
            entity.Property(x => x.Publisher).HasMaxLength(300);
            entity.Property(x => x.PublishedDate).HasMaxLength(80);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.Property(x => x.Author).HasMaxLength(300);
            entity.Property(x => x.SourceProvider).HasMaxLength(80);
            entity.Property(x => x.SourceExternalId).HasMaxLength(200);
            entity.HasOne<NovelWork>()
                .WithMany()
                .HasForeignKey(x => x.WorkId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkId, x.EditionKey }).IsUnique();
            entity.HasIndex(x => new { x.WorkId, x.IsPrimary });
            entity.HasIndex(x => x.Isbn13);
            entity.HasIndex(x => x.Isbn10);
        });

        modelBuilder.Entity<BookFile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileKey).HasMaxLength(120);
            entity.Property(x => x.FileName).HasMaxLength(500);
            entity.Property(x => x.Format).HasMaxLength(32);
            entity.Property(x => x.MediaType).HasMaxLength(120);
            entity.Property(x => x.SourceKind).HasMaxLength(80);
            entity.Property(x => x.SourceUrl).HasMaxLength(2048);
            entity.Property(x => x.ContentHash).HasMaxLength(64);
            entity.Property(x => x.StoragePath).HasMaxLength(2048);
            entity.HasOne<BookEdition>()
                .WithMany()
                .HasForeignKey(x => x.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.EditionId, x.FileKey }).IsUnique();
            entity.HasIndex(x => new { x.EditionId, x.IsPrimary });
            entity.HasIndex(x => x.ContentHash);
        });

        modelBuilder.Entity<NovelVolume>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.Property(x => x.Kind).HasMaxLength(16);
            entity.Property(x => x.SourceKey).HasMaxLength(200);
            entity.Property(x => x.SourceFileName).HasMaxLength(500);
            entity.Property(x => x.SourceStoragePath).HasMaxLength(2048);
            entity.Property(x => x.SourceContentHash).HasMaxLength(64);
            entity.Property(x => x.CoverAsset).HasMaxLength(120);
            entity.HasOne<NovelWork>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkId, x.Number }).IsUnique();
            entity.HasIndex(x => new { x.WorkId, x.SourceKey }).IsUnique();
        });

        modelBuilder.Entity<NovelChapter>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceUrl).HasMaxLength(2048);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.Property(x => x.SourceHash).HasMaxLength(64);
            entity.Property(x => x.ContentJson).HasColumnType("TEXT");
            entity.Property(x => x.GroupTitle).HasMaxLength(200);
            entity.HasOne<NovelWork>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<NovelVolume>().WithMany().HasForeignKey(x => x.VolumeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkId, x.Number }).IsUnique();
        });

        modelBuilder.Entity<NovelTranslation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TargetLanguage).HasMaxLength(16);
            entity.Property(x => x.ProviderId).HasMaxLength(80);
            entity.Property(x => x.SourceHash).HasMaxLength(64);
            entity.HasOne<NovelChapter>().WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new
            {
                x.ChapterId,
                x.TargetLanguage,
                x.ProviderId,
                x.PromptVersion,
                x.SourceHash
            }).IsUnique();
        });

        modelBuilder.Entity<NovelProgress>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
            entity.Property(x => x.AnchorLanguage).HasMaxLength(16);
            entity.Property(x => x.AnchorText).HasMaxLength(NovelTextLayout.AnchorTextLimit);
            entity.HasOne<NovelWork>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<NovelChapter>().WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProfileId, x.WorkId }).IsUnique();
        });

        modelBuilder.Entity<NovelBookmark>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
            entity.Property(x => x.Language).HasMaxLength(16);
            entity.Property(x => x.AnchorText).HasMaxLength(NovelTextLayout.AnchorTextLimit);
            entity.Property(x => x.Label).HasMaxLength(120);
            entity.Property(x => x.Style).HasMaxLength(24);
            entity.Property(x => x.Color).HasMaxLength(16);
            entity.HasOne<NovelWork>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<NovelChapter>().WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProfileId, x.WorkId, x.CreatedAt });
            entity.HasIndex(x => new { x.ProfileId, x.ChapterId });
        });

        modelBuilder.Entity<NovelBookmarkTombstone>(entity =>
        {
            entity.HasKey(x => x.BookmarkId);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
            entity.HasOne<NovelWork>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProfileId, x.WorkId });
        });

        modelBuilder.Entity<NovelHighlight>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
            entity.Property(x => x.Language).HasMaxLength(16);
            entity.Property(x => x.Text).HasMaxLength(2000);
            entity.Property(x => x.Note).HasMaxLength(2000);
            entity.HasOne<NovelWork>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<NovelChapter>().WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProfileId, x.WorkId, x.CreatedAt });
            entity.HasIndex(x => new { x.ProfileId, x.ChapterId, x.Language, x.ParagraphIndex });
        });

        modelBuilder.Entity<ReaderPreference>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
            entity.Property(x => x.ScopeKey).HasMaxLength(80);
            entity.Property(x => x.ReadingMode).HasMaxLength(24);
            entity.Property(x => x.PageTransition).HasMaxLength(24);
            entity.Property(x => x.FontFamily).HasMaxLength(100);
            entity.Property(x => x.TextAlignment).HasMaxLength(24);
            entity.Property(x => x.ChapterStyle).HasMaxLength(32);
            entity.Property(x => x.PaperStyle).HasMaxLength(32);
            entity.Property(x => x.GenreTheme).HasMaxLength(48);
            entity.Property(x => x.BackgroundAssetId).HasMaxLength(120);
            entity.Property(x => x.BackgroundMotionMode).HasMaxLength(24);
            entity.Property(x => x.BookmarkStyle).HasMaxLength(24);
            entity.Property(x => x.BookmarkColor).HasMaxLength(16);
            entity.Property(x => x.ImageFlowMode).HasMaxLength(24);
            entity.Property(x => x.ImagePageDirection).HasMaxLength(16);
            entity.Property(x => x.ImageFit).HasMaxLength(16);
            entity.Property(x => x.ImageColorScheme).HasMaxLength(16);
            entity.Property(x => x.TtsProviderId).HasMaxLength(24);
            entity.Property(x => x.TtsVoiceIds).HasMaxLength(8000);
            entity.HasIndex(x => new { x.ProfileId, x.ScopeKey }).IsUnique();
        });

        modelBuilder.Entity<NovelAnimeMapping>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AnimeProvider).HasMaxLength(80);
            entity.Property(x => x.AnimeExternalId).HasMaxLength(200);
            entity.Property(x => x.Label).HasMaxLength(200);
            entity.Property(x => x.Source).HasMaxLength(20);
            entity.HasOne<NovelWork>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkId, x.ChapterStart, x.ChapterEnd });
            entity.HasIndex(x => new { x.AnimeProvider, x.AnimeExternalId });
        });

        modelBuilder.Entity<EpisodeMediaSegment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<int>();
            entity.Property(x => x.Source).HasConversion<int>();
            entity.Property(x => x.Method).HasMaxLength(80);
            entity.Property(x => x.Version).HasMaxLength(40);
            entity.Property(x => x.MediaIdentity).HasMaxLength(64);
            entity.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.EpisodeId, x.Kind, x.Source }).IsUnique();
        });

        modelBuilder.Entity<EpisodeSegmentDetectionState>(entity =>
        {
            entity.HasKey(x => x.EpisodeId);
            entity.Property(x => x.Method).HasMaxLength(80);
            entity.Property(x => x.Version).HasMaxLength(40);
            entity.Property(x => x.MediaIdentity).HasMaxLength(64);
            entity.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AcquisitionHistoryEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventKind).HasConversion<int>();
            entity.Property(x => x.ReleaseTitle).HasMaxLength(500);
            entity.Property(x => x.ReleaseKey).HasMaxLength(300);
            entity.Property(x => x.QualityKey).HasMaxLength(80);
            entity.Property(x => x.Indexer).HasMaxLength(120);
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.HasOne<Anime>().WithMany().HasForeignKey(x => x.AnimeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.AnimeId, x.SeasonNumber, x.EpisodeNumber, x.OccurredAtUtc });
        });

        modelBuilder.Entity<AcquisitionApiKey>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.KeyPrefix).HasMaxLength(32);
            entity.Property(x => x.KeyHash).HasMaxLength(64);
            entity.HasIndex(x => x.KeyHash).IsUnique();
        });

        ConfigureMediaCore(modelBuilder);
    }

    // Universal media core (#592). Provider-independent, relational (no JSON substitute models), with
    // the unique/composite indexes the query surface and correctable mappings depend on.
    private static void ConfigureMediaCore(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Work>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MediaType).HasConversion<int>();
            entity.Property(x => x.CanonicalTitle).HasMaxLength(1000);
            entity.HasIndex(x => x.MediaType);
        });

        modelBuilder.Entity<WorkTitle>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TitleType).HasConversion<int>();
            entity.Property(x => x.Language).HasMaxLength(24);
            entity.Property(x => x.Value).HasMaxLength(1000);
            entity.Property(x => x.NormalizedValue).HasMaxLength(400);
            entity.Property(x => x.Source).HasMaxLength(80);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkId, x.TitleType, x.Language, x.NormalizedValue }).IsUnique();
            entity.HasIndex(x => x.NormalizedValue);
        });

        modelBuilder.Entity<WorkExternalIdentity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MediaType).HasConversion<int>();
            entity.Property(x => x.Provider).HasMaxLength(80);
            entity.Property(x => x.ExternalId).HasMaxLength(200);
            entity.Property(x => x.Evidence).HasMaxLength(500);
            entity.Property(x => x.ReviewState).HasConversion<int>();
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            // One provider identity resolves to at most one work; moving WorkId corrects the mapping.
            entity.HasIndex(x => new { x.Provider, x.MediaType, x.ExternalId }).IsUnique();
            entity.HasIndex(x => x.WorkId);
        });

        modelBuilder.Entity<WorkRelation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RelationType).HasConversion<int>();
            entity.Property(x => x.Source).HasMaxLength(80);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.FromWorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.ToWorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.FromWorkId, x.ToWorkId, x.RelationType }).IsUnique();
            entity.HasIndex(x => x.ToWorkId);
        });

        modelBuilder.Entity<WorkSeason>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkId, x.SeasonNumber }).IsUnique();
        });

        modelBuilder.Entity<WorkEpisode>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkSeason>().WithMany().HasForeignKey(x => x.SeasonId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => new { x.WorkId, x.SeasonNumber, x.EpisodeNumber }).IsUnique();
            entity.HasIndex(x => new { x.WorkId, x.AbsoluteNumber });
        });

        modelBuilder.Entity<WorkVolume>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkId, x.Number }).IsUnique();
        });

        modelBuilder.Entity<WorkChapter>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkVolume>().WithMany().HasForeignKey(x => x.VolumeId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => new { x.WorkId, x.Number }).IsUnique();
        });

        modelBuilder.Entity<WorkEdition>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EditionKey).HasMaxLength(200);
            entity.Property(x => x.Language).HasMaxLength(24);
            entity.Property(x => x.Format).HasMaxLength(80);
            entity.Property(x => x.Publisher).HasMaxLength(300);
            entity.Property(x => x.Isbn13).HasMaxLength(13);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkId, x.EditionKey }).IsUnique();
            entity.HasIndex(x => new { x.WorkId, x.IsPrimary });
            entity.HasIndex(x => x.Isbn13);
        });

        modelBuilder.Entity<WorkVersion>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.VersionKey).HasMaxLength(200);
            entity.Property(x => x.UnitKey).HasMaxLength(80);
            entity.Property(x => x.Quality).HasMaxLength(80);
            entity.Property(x => x.ReleaseGroup).HasMaxLength(200);
            entity.Property(x => x.Source).HasMaxLength(80);
            entity.Property(x => x.Notes).HasMaxLength(1000);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkEdition>().WithMany().HasForeignKey(x => x.EditionId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => new { x.WorkId, x.VersionKey }).IsUnique();
            entity.HasIndex(x => new { x.WorkId, x.UnitKey });
        });

        modelBuilder.Entity<WorkFieldProvenance>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FieldKey).HasMaxLength(80);
            entity.Property(x => x.Source).HasMaxLength(80);
            entity.Property(x => x.ProviderExternalId).HasMaxLength(200);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkId, x.FieldKey }).IsUnique();
        });

        modelBuilder.Entity<WorkSourceLink>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceKind).HasConversion<int>();
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            // Each legacy per-type record maps to exactly one work.
            entity.HasIndex(x => new { x.SourceKind, x.SourceId }).IsUnique();
            entity.HasIndex(x => x.WorkId);
        });

        modelBuilder.Entity<WorkIdentityChange>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ChangeType).HasConversion<int>();
            entity.Property(x => x.MediaType).HasConversion<int>();
            entity.Property(x => x.Provider).HasMaxLength(80);
            entity.Property(x => x.ExternalId).HasMaxLength(200);
            entity.Property(x => x.Actor).HasMaxLength(120);
            entity.Property(x => x.Summary).HasMaxLength(500);
            entity.Property(x => x.Details).HasMaxLength(2000);
            // No FK to Works on purpose: a merge deletes the absorbed work, and this audit log must
            // survive it. Indexed for the per-work history view and the newest-first review list.
            entity.HasIndex(x => x.TargetWorkId);
            entity.HasIndex(x => x.SourceWorkId);
            entity.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<Movie>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(500);
            entity.Property(x => x.Title).HasMaxLength(1000);
            entity.Property(x => x.TmdbId).HasMaxLength(64);
            entity.Property(x => x.ImdbId).HasMaxLength(64);
            entity.Property(x => x.LibraryPath).HasMaxLength(1024);
            // A movie's folded title+year resolves to exactly one record so re-import refreshes, never duplicates.
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<TvSeries>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(500);
            entity.Property(x => x.Title).HasMaxLength(1000);
            entity.Property(x => x.TmdbId).HasMaxLength(64);
            entity.Property(x => x.TvdbId).HasMaxLength(64);
            entity.Property(x => x.LibraryPath).HasMaxLength(1024);
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<Audiobook>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(500);
            entity.Property(x => x.Title).HasMaxLength(1000);
            entity.Property(x => x.Author).HasMaxLength(500);
            entity.Property(x => x.Narrator).HasMaxLength(500);
            entity.Property(x => x.Asin).HasMaxLength(64);
            entity.Property(x => x.LibraryPath).HasMaxLength(1024);
            // An audiobook's folded title+year resolves to exactly one record so re-import refreshes, never duplicates.
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<AudiobookFile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileKey).HasMaxLength(500);
            entity.Property(x => x.FileName).HasMaxLength(500);
            entity.Property(x => x.Format).HasMaxLength(16);
            entity.Property(x => x.StoragePath).HasMaxLength(2048);
            entity.HasOne<Audiobook>().WithMany().HasForeignKey(x => x.AudiobookId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.AudiobookId, x.FileKey }).IsUnique();
        });

        modelBuilder.Entity<AudiobookProgress>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
            entity.HasOne<Audiobook>().WithMany().HasForeignKey(x => x.AudiobookId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProfileId, x.AudiobookId }).IsUnique();
            entity.HasIndex(x => new { x.ProfileId, x.UpdatedAt });
        });

        modelBuilder.Entity<Collection>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProfileId).HasMaxLength(80);
            entity.Property(x => x.Kind).HasConversion<int>();
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(2000);
            // The nested ALL/ANY rule tree is stored as JSON; jsonb keeps it queryable and compact.
            entity.Property(x => x.RuleJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ProfileId, x.SortOrder });
        });

        modelBuilder.Entity<CollectionItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Source).HasConversion<int>();
            entity.Property(x => x.MatchReason).HasMaxLength(4000);
            entity.HasOne<Collection>().WithMany().HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Work>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            // A work appears at most once per collection, and membership reads are ordered by position.
            entity.HasIndex(x => new { x.CollectionId, x.WorkId }).IsUnique();
            entity.HasIndex(x => new { x.CollectionId, x.Position });
        });
    }
}
