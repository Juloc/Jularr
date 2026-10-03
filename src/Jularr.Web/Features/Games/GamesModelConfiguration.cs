using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jularr.Web.Features.Games;

/// <summary>Games-owned EF configuration; AppDbContext only invokes this module boundary.</summary>
public static class GamesModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new GameConfiguration());
        modelBuilder.ApplyConfiguration(new GameTitleConfiguration());
        modelBuilder.ApplyConfiguration(new GameExternalIdentityConfiguration());
        modelBuilder.ApplyConfiguration(new GameArtworkConfiguration());
        modelBuilder.ApplyConfiguration(new GamePlatformConfiguration());
        modelBuilder.ApplyConfiguration(new GameReleaseConfiguration());
        modelBuilder.ApplyConfiguration(new GameReleaseHashConfiguration());
        modelBuilder.ApplyConfiguration(new GameReleaseFileConfiguration());
    }

    private sealed class GameConfiguration : IEntityTypeConfiguration<Game>
    {
        public void Configure(EntityTypeBuilder<Game> entity)
        {
            entity.ToTable("Games");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CanonicalTitle).HasMaxLength(500);
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.Developer).HasMaxLength(300);
            entity.Property(x => x.Publisher).HasMaxLength(300);
            entity.HasIndex(x => x.CanonicalTitle);
        }
    }

    private sealed class GameTitleConfiguration : IEntityTypeConfiguration<GameTitle>
    {
        public void Configure(EntityTypeBuilder<GameTitle> entity)
        {
            entity.ToTable("GameTitles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Value).HasMaxLength(500);
            entity.Property(x => x.Kind).HasConversion<int>();
            entity.Property(x => x.LanguageTag).HasMaxLength(32);
            entity.HasOne<Game>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(x => new { x.GameId, x.Value }).IsUnique();
        }
    }

    private sealed class GameExternalIdentityConfiguration : IEntityTypeConfiguration<GameExternalIdentity>
    {
        public void Configure(EntityTypeBuilder<GameExternalIdentity> entity)
        {
            entity.ToTable("GameExternalIdentities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Provider).HasMaxLength(80);
            entity.Property(x => x.ExternalId).HasMaxLength(200);
            entity.HasOne<Game>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(x => new { x.Provider, x.ExternalId }).IsUnique();
            entity.HasIndex(x => x.GameId);
        }
    }

    private sealed class GameArtworkConfiguration : IEntityTypeConfiguration<GameArtwork>
    {
        public void Configure(EntityTypeBuilder<GameArtwork> entity)
        {
            entity.ToTable("GameArtworks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<int>();
            entity.Property(x => x.Uri).HasMaxLength(2048);
            entity.Property(x => x.Provider).HasMaxLength(80);
            entity.Property(x => x.ExternalId).HasMaxLength(200);
            entity.HasOne<Game>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(x => new { x.GameId, x.Kind });
        }
    }

    private sealed class GamePlatformConfiguration : IEntityTypeConfiguration<GamePlatform>
    {
        public void Configure(EntityTypeBuilder<GamePlatform> entity)
        {
            entity.ToTable("GamePlatforms");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(80);
            entity.Property(x => x.DisplayName).HasMaxLength(160);
            entity.HasIndex(x => x.Key).IsUnique();
        }
    }

    private sealed class GameReleaseConfiguration : IEntityTypeConfiguration<GameRelease>
    {
        public void Configure(EntityTypeBuilder<GameRelease> entity)
        {
            entity.ToTable("GameReleases");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<int>();
            entity.Property(x => x.Region).HasMaxLength(32);
            entity.Property(x => x.Revision).HasMaxLength(80);
            entity.Property(x => x.Version).HasMaxLength(80);
            entity.Property(x => x.Source).HasMaxLength(160);
            entity.Property(x => x.Format).HasMaxLength(80);
            entity.Property(x => x.LanguageTags).HasColumnType("text[]");
            entity.HasOne<Game>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<GamePlatform>().WithMany().HasForeignKey(x => x.GamePlatformId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(x => new { x.GameId, x.GamePlatformId });
        }
    }

    private sealed class GameReleaseHashConfiguration : IEntityTypeConfiguration<GameReleaseHash>
    {
        public void Configure(EntityTypeBuilder<GameReleaseHash> entity)
        {
            entity.ToTable("GameReleaseHashes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Algorithm).HasConversion<int>();
            entity.Property(x => x.Value).HasMaxLength(128);
            entity.HasOne<GameRelease>().WithMany().HasForeignKey(x => x.GameReleaseId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(x => new { x.Algorithm, x.Value }).IsUnique();
            entity.HasIndex(x => x.GameReleaseId);
        }
    }

    private sealed class GameReleaseFileConfiguration : IEntityTypeConfiguration<GameReleaseFile>
    {
        public void Configure(EntityTypeBuilder<GameReleaseFile> entity)
        {
            entity.ToTable("GameReleaseFiles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RelativePath).HasMaxLength(2048);
            entity.Property(x => x.Role).HasConversion<int>();
            entity.Property(x => x.Crc32).HasMaxLength(16);
            entity.Property(x => x.Md5).HasMaxLength(32);
            entity.Property(x => x.Sha1).HasMaxLength(40);
            entity.Property(x => x.Sha256).HasMaxLength(64);
            entity.HasOne<GameRelease>().WithMany().HasForeignKey(x => x.GameReleaseId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<LibraryRoot>().WithMany().HasForeignKey(x => x.LibraryRootId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(x => new { x.LibraryRootId, x.RelativePath }).IsUnique();
            entity.HasIndex(x => new { x.GameReleaseId, x.Sequence });
        }
    }
}
