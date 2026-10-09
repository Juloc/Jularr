using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.MediaCore;

/// <summary>
/// Media-type-agnostic structure upserts for the universal core (#592): seasons/episodes (with anime
/// absolute + season numbering and specials), volumes/chapters, and separately-modelled
/// editions/versions. Every method is idempotent on its natural key so re-import refreshes rather than
/// duplicates.
/// </summary>
public sealed class WorkStructureService(AppDbContext db)
{
    /// <summary>Upserts a season, keyed by (work, season number).</summary>
    public async Task<WorkSeason> AddOrUpdateSeasonAsync(
        long workId,
        int seasonNumber,
        string? title,
        CancellationToken cancellationToken)
    {
        var existing = await db.Set<WorkSeason>()
            .FirstOrDefaultAsync(x => x.WorkId == workId && x.SeasonNumber == seasonNumber, cancellationToken);

        if (existing is not null)
        {
            existing.Title = title;
            existing.IsSpecial = seasonNumber == 0;
            await db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var season = new WorkSeason
        {
            WorkId = workId,
            SeasonNumber = seasonNumber,
            Title = title,
            IsSpecial = seasonNumber == 0
        };
        db.Set<WorkSeason>().Add(season);
        await db.SaveChangesAsync(cancellationToken);
        return season;
    }

    /// <summary>
    /// Upserts an episode, keyed by (work, season number, episode number). Preserves absolute numbering
    /// and the special flag, so anime that mix season and absolute numbering and carry specials round-trip.
    /// </summary>
    public async Task<WorkEpisode> AddOrUpdateEpisodeAsync(
        long workId,
        int seasonNumber,
        int episodeNumber,
        int? absoluteNumber,
        bool isSpecial,
        string? title,
        DateTime? airedAt,
        Guid? seasonId,
        CancellationToken cancellationToken)
    {
        var existing = await db.Set<WorkEpisode>()
            .FirstOrDefaultAsync(
                x => x.WorkId == workId && x.SeasonNumber == seasonNumber && x.EpisodeNumber == episodeNumber,
                cancellationToken);

        if (existing is not null)
        {
            existing.AbsoluteNumber = absoluteNumber;
            existing.IsSpecial = isSpecial;
            existing.Title = title;
            existing.AiredAt = airedAt;
            existing.SeasonId = seasonId;
            await db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var episode = new WorkEpisode
        {
            WorkId = workId,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            AbsoluteNumber = absoluteNumber,
            IsSpecial = isSpecial,
            Title = title,
            AiredAt = airedAt,
            SeasonId = seasonId
        };
        db.Set<WorkEpisode>().Add(episode);
        await db.SaveChangesAsync(cancellationToken);
        return episode;
    }

    /// <summary>Upserts a volume, keyed by (work, number).</summary>
    public async Task<WorkVolume> AddOrUpdateVolumeAsync(
        long workId,
        int number,
        string? title,
        CancellationToken cancellationToken)
    {
        var existing = await db.Set<WorkVolume>()
            .FirstOrDefaultAsync(x => x.WorkId == workId && x.Number == number, cancellationToken);

        if (existing is not null)
        {
            existing.Title = title;
            await db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var volume = new WorkVolume { WorkId = workId, Number = number, Title = title };
        db.Set<WorkVolume>().Add(volume);
        await db.SaveChangesAsync(cancellationToken);
        return volume;
    }

    /// <summary>Upserts a chapter, keyed by (work, number). Decimal numbers allow "10.5" specials.</summary>
    public async Task<WorkChapter> AddOrUpdateChapterAsync(
        long workId,
        double number,
        string? title,
        bool isSpecial,
        Guid? volumeId,
        CancellationToken cancellationToken)
    {
        var existing = await db.Set<WorkChapter>()
            .FirstOrDefaultAsync(x => x.WorkId == workId && x.Number == number, cancellationToken);

        if (existing is not null)
        {
            existing.Title = title;
            existing.IsSpecial = isSpecial;
            existing.VolumeId = volumeId;
            await db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var chapter = new WorkChapter
        {
            WorkId = workId,
            Number = number,
            Title = title,
            IsSpecial = isSpecial,
            VolumeId = volumeId
        };
        db.Set<WorkChapter>().Add(chapter);
        await db.SaveChangesAsync(cancellationToken);
        return chapter;
    }

    /// <summary>Upserts an edition (editorial variant), keyed by (work, edition key).</summary>
    public async Task<WorkEdition> AddOrUpdateEditionAsync(
        long workId,
        string editionKey,
        string language,
        string? format,
        string? publisher,
        string? isbn13,
        string? title,
        bool isPrimary,
        CancellationToken cancellationToken)
    {
        var key = editionKey.Trim();
        var existing = await db.Set<WorkEdition>()
            .FirstOrDefaultAsync(x => x.WorkId == workId && x.EditionKey == key, cancellationToken);

        if (isPrimary)
        {
            await ClearPrimaryEditionAsync(workId, cancellationToken);
        }

        if (existing is not null)
        {
            existing.Language = (language ?? "und").Trim().ToLowerInvariant();
            existing.Format = format;
            existing.Publisher = publisher;
            existing.Isbn13 = isbn13;
            existing.Title = title;
            existing.IsPrimary = isPrimary || existing.IsPrimary;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var edition = new WorkEdition
        {
            WorkId = workId,
            EditionKey = key,
            Language = (language ?? "und").Trim().ToLowerInvariant(),
            Format = format,
            Publisher = publisher,
            Isbn13 = isbn13,
            Title = title,
            IsPrimary = isPrimary
        };
        db.Set<WorkEdition>().Add(edition);
        await db.SaveChangesAsync(cancellationToken);
        return edition;
    }

    /// <summary>Upserts a concrete version (acquirable variant), keyed by (work, version key).</summary>
    public async Task<WorkVersion> AddOrUpdateVersionAsync(
        long workId,
        string versionKey,
        string? unitKey,
        Guid? editionId,
        string? quality,
        string? releaseGroup,
        string? source,
        string? notes,
        CancellationToken cancellationToken)
    {
        var key = versionKey.Trim();
        var existing = await db.Set<WorkVersion>()
            .FirstOrDefaultAsync(x => x.WorkId == workId && x.VersionKey == key, cancellationToken);

        if (existing is not null)
        {
            existing.UnitKey = unitKey;
            existing.EditionId = editionId;
            existing.Quality = quality;
            existing.ReleaseGroup = releaseGroup;
            existing.Source = source;
            existing.Notes = notes;
            await db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var version = new WorkVersion
        {
            WorkId = workId,
            VersionKey = key,
            UnitKey = unitKey,
            EditionId = editionId,
            Quality = quality,
            ReleaseGroup = releaseGroup,
            Source = source,
            Notes = notes
        };
        db.Set<WorkVersion>().Add(version);
        await db.SaveChangesAsync(cancellationToken);
        return version;
    }

    private async Task ClearPrimaryEditionAsync(long workId, CancellationToken cancellationToken)
    {
        var current = await db.Set<WorkEdition>()
            .Where(x => x.WorkId == workId && x.IsPrimary)
            .ToListAsync(cancellationToken);
        foreach (var edition in current)
        {
            edition.IsPrimary = false;
        }
    }
}
