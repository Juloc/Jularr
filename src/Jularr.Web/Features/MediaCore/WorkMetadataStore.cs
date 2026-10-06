using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Jularr.Web.Features.MediaCore;

/// <summary>A claimed spool entry: the worker owns it until its lease runs out.</summary>
public sealed record WorkMetadataRefreshClaim(long Id, Guid WorkId, string Locale, WorkMetadataRefreshPriority Priority, WorkMetadataRefreshStatus Status, int Attempts, WorkMediaType MediaType);

/// <summary>A localized value as the spool needs it to decide whether a provider value may replace it.</summary>
public sealed record WorkLocalizedValueState(WorkLocalizedField Field, int Position, string Source, bool IsManualOverride);

/// <summary>A stored artwork variant.</summary>
public sealed record WorkArtworkRow(long Id, WorkArtworkSlot Slot, string Language, string Source, string ProviderFilePath, int? Width, int? Height, double? VoteAverage, string? CacheKey, bool IsManualOverride);

/// <summary>Every persisted metadata row of one Work, all locales, for the detail read model to resolve.</summary>
public sealed record WorkMetadataRows(
    WorkMetadataFacts? Facts,
    IReadOnlyList<WorkLocalizedValue> Values,
    IReadOnlyList<WorkCredit> Credits,
    IReadOnlyList<WorkArtworkRow> Artwork,
    DateTime? LastSucceededAt);

/// <summary>The card-sized metadata of one Work in a Library page: a locally cached poster or backdrop variant and the rating.</summary>
public sealed record WorkCardMetadataRow(Guid WorkId, long? ArtworkId, WorkArtworkSlot? Slot, string? Language, string? CacheKey, double? VoteAverage, double? Rating);

/// <summary>
/// The persistence owner of Work metadata and artwork (#820): explicit, parameterized PostgreSQL for the localized values, the
/// language-neutral facts, credits, artwork variants and the metadata spool. It holds no business rules: which value wins, what to
/// download and when to retry are decided by <see cref="WorkMetadataRefresher"/>; reads are side-effect free.
/// </summary>
public sealed class WorkMetadataStore(AppDbContext db)
{
    /// <summary>Upper bound of credits a Work keeps; the provider adapter already trims to fewer.</summary>
    public const int MaxCredits = 40;

    /// <summary>
    /// Puts one <c>(Work, locale)</c> on the spool, or promotes it. A queued entry takes the higher priority; one that never ran is
    /// also made due now. A fresh or permanently failed entry is left alone until its own time comes, so opening a Work never refetches
    /// fresh data and never bypasses a failure's backoff. Only Movie and Series Works are spooled (the media types with a metadata
    /// provider); for any other Work, or an unknown id, nothing is written.
    /// </summary>
    public Task EnqueueAsync(Guid workId, string locale, WorkMetadataRefreshPriority priority, DateTime nowUtc, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "WorkMetadataRefreshes" AS refresh ("WorkId", "Locale", "Priority", "Status", "Attempts", "NextAttemptAt", "CreatedAt", "UpdatedAt")
            SELECT work."Id", {locale}, {(int)priority}, {(int)WorkMetadataRefreshStatus.Queued}, 0, {nowUtc}, {nowUtc}, {nowUtc}
            FROM "Works" AS work
            WHERE work."Id" = {workId} AND work."MediaType" IN ({(int)WorkMediaType.Movie}, {(int)WorkMediaType.Series})
            ON CONFLICT ("WorkId", "Locale") DO UPDATE SET
                "Priority" = LEAST(refresh."Priority", excluded."Priority"),
                "NextAttemptAt" = CASE WHEN refresh."LastAttemptAt" IS NULL THEN LEAST(refresh."NextAttemptAt", excluded."NextAttemptAt") ELSE refresh."NextAttemptAt" END,
                "UpdatedAt" = excluded."UpdatedAt"
            WHERE refresh."Status" = {(int)WorkMetadataRefreshStatus.Queued} OR refresh."NextAttemptAt" <= excluded."NextAttemptAt"
            """,
            cancellationToken);

    /// <summary>
    /// The idempotent backfill: every durable Movie/Series Work with a TMDB identity that has no spool entry for the locale gets one,
    /// prioritized by what the Work is to the household. A Work with a file or playback progress is imported/in progress; one without a
    /// legacy library record was materialized by a Request (the only other path that creates a TMDB-identified Work); the rest is Library.
    /// Returns how many entries were added; a rerun adds none.
    /// </summary>
    public Task<int> EnqueueDurableWorksAsync(string locale, DateTime nowUtc, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "WorkMetadataRefreshes" ("WorkId", "Locale", "Priority", "Status", "Attempts", "NextAttemptAt", "CreatedAt", "UpdatedAt")
            SELECT work."Id", {locale},
                CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM "MediaAssets" AS asset
                        INNER JOIN "StoredFiles" AS stored ON stored."MediaAssetId" = asset."Id"
                        WHERE asset."WorkId" = work."Id")
                      OR EXISTS (SELECT 1 FROM "MediaProgress" AS progress WHERE progress."WorkId" = work."Id")
                        THEN {(int)WorkMetadataRefreshPriority.Imported}
                    WHEN NOT EXISTS (
                        SELECT 1
                        FROM "WorkSourceLinks" AS link
                        WHERE link."WorkId" = work."Id" AND link."SourceKind" IN ({(int)WorkSourceKind.Movie}, {(int)WorkSourceKind.Series}))
                        THEN {(int)WorkMetadataRefreshPriority.Requested}
                    ELSE {(int)WorkMetadataRefreshPriority.Library}
                END,
                {(int)WorkMetadataRefreshStatus.Queued}, 0, {nowUtc}, {nowUtc}, {nowUtc}
            FROM "Works" AS work
            WHERE work."MediaType" IN ({(int)WorkMediaType.Movie}, {(int)WorkMediaType.Series})
              AND EXISTS (
                  SELECT 1
                  FROM "WorkExternalIdentities" AS identity
                  WHERE identity."WorkId" = work."Id" AND identity."Provider" = 'tmdb' AND identity."MediaType" = work."MediaType")
            ON CONFLICT ("WorkId", "Locale") DO NOTHING
            """,
            cancellationToken);

    /// <summary>
    /// Claims the most urgent due entry of an enabled media type and leases it until <paramref name="leaseUntilUtc"/>: a run that dies
    /// with the process becomes due again when the lease ends. <c>SKIP LOCKED</c> keeps two claimers from taking the same entry.
    /// </summary>
    public async Task<WorkMetadataRefreshClaim?> ClaimNextDueAsync(IReadOnlyCollection<WorkMediaType> mediaTypes, DateTime nowUtc, DateTime leaseUntilUtc, CancellationToken cancellationToken)
    {
        var types = mediaTypes.Select(x => (int)x).ToArray();
        var rows = await db.Database.SqlQuery<ClaimRow>(
                $"""
                UPDATE "WorkMetadataRefreshes" AS refresh
                SET "NextAttemptAt" = {leaseUntilUtc}, "LastAttemptAt" = {nowUtc}, "UpdatedAt" = {nowUtc}
                FROM (
                    SELECT candidate."Id", work."MediaType"
                    FROM "WorkMetadataRefreshes" AS candidate
                    INNER JOIN "Works" AS work ON work."Id" = candidate."WorkId"
                    WHERE candidate."NextAttemptAt" <= {nowUtc} AND work."MediaType" = ANY({types})
                    ORDER BY candidate."Priority", candidate."NextAttemptAt", candidate."Id"
                    LIMIT 1
                    FOR UPDATE OF candidate SKIP LOCKED
                ) AS due
                WHERE refresh."Id" = due."Id"
                RETURNING refresh."Id", refresh."WorkId", refresh."Locale", refresh."Priority", refresh."Status", refresh."Attempts", due."MediaType"
                """)
            .ToListAsync(cancellationToken);
        return rows.Count == 0
            ? null
            : rows.Select(x => new WorkMetadataRefreshClaim(x.Id, x.WorkId, x.Locale, (WorkMetadataRefreshPriority)x.Priority, (WorkMetadataRefreshStatus)x.Status, x.Attempts, (WorkMediaType)x.MediaType)).Single();
    }

    /// <summary>When the earliest entry of an enabled media type falls due; null when the spool has none.</summary>
    public async Task<DateTime?> FindNextDueAtAsync(IReadOnlyCollection<WorkMediaType> mediaTypes, CancellationToken cancellationToken)
    {
        var types = mediaTypes.Select(x => (int)x).ToArray();
        return (await db.Database.SqlQuery<DateTime?>(
                $"""
                SELECT MIN(refresh."NextAttemptAt") AS "Value"
                FROM "WorkMetadataRefreshes" AS refresh
                INNER JOIN "Works" AS work ON work."Id" = refresh."WorkId"
                WHERE work."MediaType" = ANY({types})
                """)
            .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }

    /// <summary>Records the outcome of a run and when the entry is due next.</summary>
    public Task RecordRunAsync(long id, WorkMetadataRefreshStatus status, WorkMetadataRefreshPriority priority, int attempts, DateTime nextAttemptAtUtc, string? lastError, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var succeededAt = Param("@succeededAt", status == WorkMetadataRefreshStatus.Fresh ? nowUtc : null, NpgsqlDbType.TimestampTz);
        var error = Param("@lastError", lastError, NpgsqlDbType.Varchar);
        return db.Database.ExecuteSqlAsync(
            $"""
            UPDATE "WorkMetadataRefreshes"
            SET "Status" = {(int)status}, "Priority" = {(int)priority}, "Attempts" = {attempts}, "NextAttemptAt" = {nextAttemptAtUtc},
                "LastSucceededAt" = COALESCE({succeededAt}, "LastSucceededAt"), "LastError" = {error}, "UpdatedAt" = {nowUtc}
            WHERE "Id" = {id}
            """,
            cancellationToken);
    }

    /// <summary>The TMDB identity the spool fetches a Work by, primary first; null when the Work has none.</summary>
    public async Task<string?> FindTmdbIdAsync(Guid workId, WorkMediaType mediaType, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<string>(
                $"""
                SELECT "ExternalId" AS "Value"
                FROM "WorkExternalIdentities"
                WHERE "WorkId" = {workId} AND "MediaType" = {(int)mediaType} AND "Provider" = 'tmdb'
                ORDER BY "IsPrimary" DESC, "ExternalId"
                LIMIT 1
                """)
            .ToListAsync(cancellationToken))
        .SingleOrDefault();

    public async Task<IReadOnlyList<WorkLocalizedValueState>> LoadLocalizedStatesAsync(Guid workId, string locale, CancellationToken cancellationToken) =>
        [
            .. (await db.Database.SqlQuery<LocalizedStateRow>(
                    $"""
                    SELECT "Field", "Position", "Source", "IsManualOverride"
                    FROM "WorkLocalizedValues"
                    WHERE "WorkId" = {workId} AND "Locale" = {locale}
                    """)
                .ToListAsync(cancellationToken))
            .Select(x => new WorkLocalizedValueState((WorkLocalizedField)x.Field, x.Position, x.Source, x.IsManualOverride))
        ];

    /// <summary>Replaces one localized field of one locale as a whole (all positions of a list field) with provider values.</summary>
    public async Task ReplaceLocalizedFieldAsync(
        Guid workId,
        string locale,
        WorkLocalizedField field,
        IReadOnlyList<string> values,
        string source,
        string providerExternalId,
        int fallbackPriority,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "WorkLocalizedValues" WHERE "WorkId" = {workId} AND "Locale" = {locale} AND "Field" = {(int)field}""", cancellationToken);
        var texts = values.ToArray();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "WorkLocalizedValues"
                ("WorkId", "Locale", "Field", "Position", "Value", "Origin", "Source", "ProviderExternalId", "Confidence", "FallbackPriority", "IsManualOverride", "FetchedAt", "UpdatedAt")
            SELECT {workId}, {locale}, {(int)field}, item.ordinality - 1, item.value, {(int)WorkMetadataValueOrigin.ProviderLocalized}, {source}, {providerExternalId}, 1.0,
                {fallbackPriority}, FALSE, {nowUtc}, {nowUtc}
            FROM unnest({texts}::text[]) WITH ORDINALITY AS item(value, ordinality)
            """,
            cancellationToken);
    }

    public async Task<WorkMetadataFacts?> LoadFactsAsync(Guid workId, CancellationToken cancellationToken) =>
        (await db.Set<WorkMetadataFacts>().FromSql(
                $"""
                SELECT "Id", "WorkId", "OriginalTitle", "OriginalLanguage", "ReleaseDate", "RuntimeMinutes", "Rating", "RatingCount", "Certification",
                    "CertificationCountry", "Studios", "ProductionCountries", "UpdatedAt"
                FROM "WorkMetadataFacts"
                WHERE "WorkId" = {workId}
                """)
            .AsNoTracking().ToListAsync(cancellationToken))
        .SingleOrDefault();

    public Task UpsertFactsAsync(WorkMetadataFacts facts, CancellationToken cancellationToken)
    {
        var originalTitle = Param("@originalTitle", facts.OriginalTitle, NpgsqlDbType.Varchar);
        var originalLanguage = Param("@originalLanguage", facts.OriginalLanguage, NpgsqlDbType.Varchar);
        var releaseDate = Param("@releaseDate", facts.ReleaseDate, NpgsqlDbType.Date);
        var runtime = Param("@runtime", facts.RuntimeMinutes, NpgsqlDbType.Integer);
        var rating = Param("@rating", facts.Rating, NpgsqlDbType.Double);
        var ratingCount = Param("@ratingCount", facts.RatingCount, NpgsqlDbType.Integer);
        var certification = Param("@certification", facts.Certification, NpgsqlDbType.Varchar);
        var certificationCountry = Param("@certificationCountry", facts.CertificationCountry, NpgsqlDbType.Varchar);
        return db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "WorkMetadataFacts"
                ("WorkId", "OriginalTitle", "OriginalLanguage", "ReleaseDate", "RuntimeMinutes", "Rating", "RatingCount", "Certification", "CertificationCountry",
                 "Studios", "ProductionCountries", "UpdatedAt")
            VALUES ({facts.WorkId}, {originalTitle}, {originalLanguage}, {releaseDate}, {runtime}, {rating}, {ratingCount}, {certification}, {certificationCountry},
                {facts.Studios}, {facts.ProductionCountries}, {facts.UpdatedAt})
            ON CONFLICT ("WorkId") DO UPDATE SET
                "OriginalTitle" = excluded."OriginalTitle", "OriginalLanguage" = excluded."OriginalLanguage", "ReleaseDate" = excluded."ReleaseDate",
                "RuntimeMinutes" = excluded."RuntimeMinutes", "Rating" = excluded."Rating", "RatingCount" = excluded."RatingCount",
                "Certification" = excluded."Certification", "CertificationCountry" = excluded."CertificationCountry", "Studios" = excluded."Studios",
                "ProductionCountries" = excluded."ProductionCountries", "UpdatedAt" = excluded."UpdatedAt"
            """,
            cancellationToken);
    }

    /// <summary>Replaces the credits of a Work with the provider's current billing.</summary>
    public async Task ReplaceCreditsAsync(Guid workId, IReadOnlyList<WorkCreditCandidate> credits, string source, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var bounded = credits.Take(MaxCredits).ToArray();
        var kinds = bounded.Select(x => (int)x.Kind).ToArray();
        var names = bounded.Select(x => x.Name).ToArray();
        var roles = bounded.Select(x => x.Role).ToArray();
        var people = bounded.Select(x => x.ProviderPersonId).ToArray();
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "WorkCredits" WHERE "WorkId" = {workId}""", cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "WorkCredits" ("WorkId", "Kind", "Position", "Name", "Role", "Source", "ProviderPersonId", "FetchedAt")
            SELECT {workId}, item.kind,
                ROW_NUMBER() OVER (PARTITION BY item.kind ORDER BY item.ordinality) - 1,
                item.name, item.role, {source}, item.person, {nowUtc}
            FROM unnest({kinds}::integer[], {names}::text[], {roles}::text[], {people}::text[]) WITH ORDINALITY AS item(kind, name, role, person, ordinality)
            """,
            cancellationToken);
    }

    public async Task<IReadOnlyList<WorkArtworkRow>> LoadArtworkAsync(Guid workId, CancellationToken cancellationToken) =>
        [
            .. (await db.Database.SqlQuery<ArtworkDbRow>(
                    $"""
                    SELECT "Id", "Slot", "Language", "Source", "ProviderFilePath", "Width", "Height", "VoteAverage", "CacheKey", "IsManualOverride"
                    FROM "WorkArtwork"
                    WHERE "WorkId" = {workId}
                    """)
                .ToListAsync(cancellationToken))
            .Select(ToArtwork)
        ];

    /// <summary>
    /// Stores the chosen provider image for <c>(Work, slot, language)</c> with its local derivative. An owner-chosen variant is never
    /// replaced. Returns false when the variant was protected.
    /// </summary>
    public async Task<bool> UpsertArtworkAsync(Guid workId, WorkArtworkCandidate candidate, string source, string cacheKey, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var width = Param("@width", candidate.Width, NpgsqlDbType.Integer);
        var height = Param("@height", candidate.Height, NpgsqlDbType.Integer);
        var voteAverage = Param("@voteAverage", candidate.VoteAverage, NpgsqlDbType.Double);
        var voteCount = Param("@voteCount", candidate.VoteCount, NpgsqlDbType.Integer);
        var written = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "WorkArtwork" AS artwork
                ("WorkId", "Slot", "Language", "Source", "ProviderFilePath", "Width", "Height", "VoteAverage", "VoteCount", "CacheKey", "IsManualOverride",
                 "FetchedAt", "CachedAt", "UpdatedAt")
            VALUES ({workId}, {(int)candidate.Slot}, {candidate.Language}, {source}, {candidate.ProviderFilePath}, {width}, {height}, {voteAverage}, {voteCount}, {cacheKey},
                FALSE, {nowUtc}, {nowUtc}, {nowUtc})
            ON CONFLICT ("WorkId", "Slot", "Language") DO UPDATE SET
                "Source" = excluded."Source", "ProviderFilePath" = excluded."ProviderFilePath", "Width" = excluded."Width", "Height" = excluded."Height",
                "VoteAverage" = excluded."VoteAverage", "VoteCount" = excluded."VoteCount", "CacheKey" = excluded."CacheKey", "FetchedAt" = excluded."FetchedAt",
                "CachedAt" = excluded."CachedAt", "UpdatedAt" = excluded."UpdatedAt"
            WHERE NOT artwork."IsManualOverride"
            """,
            cancellationToken);
        return written > 0;
    }

    /// <summary>Every persisted metadata row of one Work. Bounded: values per locale and field, credits and artwork variants are capped.</summary>
    public async Task<WorkMetadataRows> LoadAsync(Guid workId, CancellationToken cancellationToken)
    {
        var facts = await LoadFactsAsync(workId, cancellationToken);
        var values = await db.Set<WorkLocalizedValue>().FromSql(
                $"""
                SELECT "Id", "WorkId", "Locale", "Field", "Position", "Value", "Origin", "Source", "SourceLocale", "ProviderExternalId", "Confidence",
                    "FallbackPriority", "IsManualOverride", "FetchedAt", "UpdatedAt"
                FROM "WorkLocalizedValues"
                WHERE "WorkId" = {workId}
                ORDER BY "Locale", "Field", "Position"
                LIMIT 1000
                """)
            .AsNoTracking().ToListAsync(cancellationToken);
        var credits = await db.Set<WorkCredit>().FromSql(
                $"""
                SELECT "Id", "WorkId", "Kind", "Position", "Name", "Role", "Source", "ProviderPersonId", "FetchedAt"
                FROM "WorkCredits"
                WHERE "WorkId" = {workId}
                ORDER BY "Kind", "Position"
                LIMIT {MaxCredits}
                """)
            .AsNoTracking().ToListAsync(cancellationToken);
        var artwork = await LoadArtworkAsync(workId, cancellationToken);
        var succeeded = (await db.Database.SqlQuery<DateTime?>(
                $"""SELECT MAX("LastSucceededAt") AS "Value" FROM "WorkMetadataRefreshes" WHERE "WorkId" = {workId}""")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        return new WorkMetadataRows(facts, values, credits, [.. artwork.Where(x => x.CacheKey is not null)], succeeded);
    }

    /// <summary>
    /// The locally cached poster and backdrop variants plus the rating of every given Work in one query, for a Library page. Works
    /// without any persisted metadata yield no row.
    /// </summary>
    public async Task<IReadOnlyList<WorkCardMetadataRow>> LoadCardMetadataAsync(IReadOnlyCollection<Guid> workIds, CancellationToken cancellationToken)
    {
        if (workIds.Count == 0)
        {
            return [];
        }

        var ids = workIds.ToArray();
        var rows = await db.Database.SqlQuery<CardDbRow>(
                $"""
                SELECT requested.work_id AS "WorkId", artwork."Id" AS "ArtworkId", artwork."Slot", artwork."Language", artwork."CacheKey", artwork."VoteAverage",
                    facts."Rating"
                FROM unnest({ids}::uuid[]) AS requested(work_id)
                LEFT JOIN "WorkMetadataFacts" AS facts ON facts."WorkId" = requested.work_id
                LEFT JOIN "WorkArtwork" AS artwork
                    ON artwork."WorkId" = requested.work_id
                   AND artwork."CacheKey" IS NOT NULL
                   AND artwork."Slot" IN ({(int)WorkArtworkSlot.Poster}, {(int)WorkArtworkSlot.Backdrop})
                WHERE facts."Id" IS NOT NULL OR artwork."Id" IS NOT NULL
                """)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(x => new WorkCardMetadataRow(x.WorkId, x.ArtworkId, (WorkArtworkSlot?)x.Slot, x.Language, x.CacheKey, x.VoteAverage, x.Rating))];
    }

    /// <summary>The media type and local derivative of one artwork variant of a Work; null when the Work has no such cached variant.</summary>
    public async Task<(WorkMediaType MediaType, string CacheKey)?> FindArtworkFileAsync(Guid workId, long artworkId, CancellationToken cancellationToken)
    {
        var row = (await db.Database.SqlQuery<ArtworkFileRow>(
                $"""
                SELECT work."MediaType", artwork."CacheKey"
                FROM "WorkArtwork" AS artwork
                INNER JOIN "Works" AS work ON work."Id" = artwork."WorkId"
                WHERE artwork."Id" = {artworkId} AND artwork."WorkId" = {workId} AND artwork."CacheKey" IS NOT NULL
                """)
            .ToListAsync(cancellationToken))
            .SingleOrDefault();
        return row is null ? null : ((WorkMediaType)row.MediaType, row.CacheKey);
    }

    /// <summary>Which of the given cache keys a variant still references, for the orphan sweep of the artwork cache.</summary>
    public async Task<IReadOnlySet<string>> FindReferencedCacheKeysAsync(IReadOnlyCollection<string> cacheKeys, CancellationToken cancellationToken)
    {
        var keys = cacheKeys.ToArray();
        return (await db.Database.SqlQuery<string>($"""SELECT "CacheKey" AS "Value" FROM "WorkArtwork" WHERE "CacheKey" = ANY({keys}::text[])""").ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Merge step for <see cref="WorkService.MergeWorksAsync"/>: the survivor keeps every metadata group it already has; groups only the
    /// absorbed Work has (a locale's field, the facts, the credits, an artwork slot/language, a spool locale) move over, the rest is
    /// deleted. Runs inside the merge transaction. Deleted variants leave their cache files to the orphan sweep.
    /// </summary>
    public async Task MoveForMergeAsync(Guid sourceWorkId, Guid targetWorkId, CancellationToken cancellationToken)
    {
        // A refresh writing rows for the absorbed Work holds a key-share lock on it: wait for it to commit so its rows are moved too,
        // and make every later refresh write for that Work wait for the merge (and then fail on the deleted Work, not the merge).
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Works" WHERE "Id" = {sourceWorkId} FOR UPDATE""", cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "WorkLocalizedValues" AS source
            WHERE source."WorkId" = {sourceWorkId}
              AND EXISTS (SELECT 1 FROM "WorkLocalizedValues" AS target WHERE target."WorkId" = {targetWorkId} AND target."Locale" = source."Locale" AND target."Field" = source."Field")
            """,
            cancellationToken);
        await db.Database.ExecuteSqlAsync($"""UPDATE "WorkLocalizedValues" SET "WorkId" = {targetWorkId} WHERE "WorkId" = {sourceWorkId}""", cancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "WorkMetadataFacts" WHERE "WorkId" = {sourceWorkId} AND EXISTS (SELECT 1 FROM "WorkMetadataFacts" WHERE "WorkId" = {targetWorkId})
            """,
            cancellationToken);
        await db.Database.ExecuteSqlAsync($"""UPDATE "WorkMetadataFacts" SET "WorkId" = {targetWorkId} WHERE "WorkId" = {sourceWorkId}""", cancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "WorkCredits" WHERE "WorkId" = {sourceWorkId} AND EXISTS (SELECT 1 FROM "WorkCredits" WHERE "WorkId" = {targetWorkId})
            """,
            cancellationToken);
        await db.Database.ExecuteSqlAsync($"""UPDATE "WorkCredits" SET "WorkId" = {targetWorkId} WHERE "WorkId" = {sourceWorkId}""", cancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "WorkArtwork" AS source
            WHERE source."WorkId" = {sourceWorkId}
              AND EXISTS (SELECT 1 FROM "WorkArtwork" AS target WHERE target."WorkId" = {targetWorkId} AND target."Slot" = source."Slot" AND target."Language" = source."Language")
            """,
            cancellationToken);
        await db.Database.ExecuteSqlAsync($"""UPDATE "WorkArtwork" SET "WorkId" = {targetWorkId} WHERE "WorkId" = {sourceWorkId}""", cancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "WorkMetadataRefreshes" AS source
            WHERE source."WorkId" = {sourceWorkId}
              AND EXISTS (SELECT 1 FROM "WorkMetadataRefreshes" AS target WHERE target."WorkId" = {targetWorkId} AND target."Locale" = source."Locale")
            """,
            cancellationToken);
        await db.Database.ExecuteSqlAsync($"""UPDATE "WorkMetadataRefreshes" SET "WorkId" = {targetWorkId} WHERE "WorkId" = {sourceWorkId}""", cancellationToken);
    }

    // A null bound without a type reaches PostgreSQL as text; typing it keeps nullable dates, numbers and COALESCE well-defined.
    private static NpgsqlParameter Param(string name, object? value, NpgsqlDbType type) => new(name, type) { Value = value ?? DBNull.Value };

    private static WorkArtworkRow ToArtwork(ArtworkDbRow row) =>
        new(row.Id, (WorkArtworkSlot)row.Slot, row.Language, row.Source, row.ProviderFilePath, row.Width, row.Height, row.VoteAverage, row.CacheKey, row.IsManualOverride);

    private sealed record ClaimRow(long Id, Guid WorkId, string Locale, int Priority, int Status, int Attempts, int MediaType);

    private sealed record LocalizedStateRow(int Field, int Position, string Source, bool IsManualOverride);

    private sealed record ArtworkDbRow(long Id, int Slot, string Language, string Source, string ProviderFilePath, int? Width, int? Height, double? VoteAverage, string? CacheKey, bool IsManualOverride);

    private sealed record CardDbRow(Guid WorkId, long? ArtworkId, int? Slot, string? Language, string? CacheKey, double? VoteAverage, double? Rating);

    private sealed record ArtworkFileRow(int MediaType, string CacheKey);
}
