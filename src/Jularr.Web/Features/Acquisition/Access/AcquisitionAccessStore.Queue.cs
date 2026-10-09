using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

public sealed record AdminRequestQueue(AdminRequestPage Page, bool AnyRequests, IReadOnlyList<string> RequesterIds);

public sealed partial class AcquisitionAccessStore
{
    /// <summary>Bounded title lookup for Admin search without recomputing queue facets or materializing request payloads.</summary>
    public async Task<IReadOnlyList<string>> SearchTitlesAsync(string query, IReadOnlyCollection<MediaAcquisitionKind> kinds, CancellationToken cancellationToken)
    {
        var search = query.Trim().ToLowerInvariant();
        if (search.Length == 0 || kinds.Count == 0)
        {
            return [];
        }

        var names = kinds.Select(AcquisitionAccessNames.Kind).ToArray();
        return await db.Database.SqlQuery<string>($"""
            SELECT DISTINCT "Title" AS "Value"
            FROM "AcquisitionRequests"
            WHERE "Kind" = ANY({names})
                AND (strpos(lower("Title"), {search}) > 0 OR strpos(lower(COALESCE("Subtitle", '')), {search}) > 0)
            ORDER BY "Title"
            LIMIT 8
            """).ToListAsync(cancellationToken);
    }

    private const string QueueSource = """
        WITH source AS (
            SELECT r.*, COALESCE(r."PayloadJson"::jsonb, '{}'::jsonb) AS payload
            FROM "AcquisitionRequests" r WHERE r."Kind" = ANY(@kinds)
        ), queue AS (
            SELECT source.*, NULLIF(payload->>'audioLanguage', '') AS language,
                ARRAY(
                    SELECT DISTINCT number FROM (
                        SELECT value::integer AS number
                        FROM jsonb_array_elements_text(CASE WHEN "Kind" = 'anime' AND payload->>'scope' = 'seasons' THEN payload->'seasons' ELSE '[]'::jsonb END)
                        UNION ALL
                        SELECT (value->>'season')::integer
                        FROM jsonb_array_elements(CASE WHEN "Kind" = 'anime' AND payload->>'scope' = 'episodes' THEN payload->'episodes' ELSE '[]'::jsonb END)
                        UNION ALL
                        SELECT s."SeasonNumber" FROM "WorkSeasons" s
                        WHERE "Kind" = 'tv' AND s."WorkId" = (payload->>'workId')::bigint
                            AND payload->'requested'->'seasonIds' ? s."Id"::text
                        UNION ALL
                        SELECT e."SeasonNumber" FROM "WorkEpisodes" e
                        WHERE "Kind" = 'tv' AND e."WorkId" = (payload->>'workId')::bigint
                            AND payload->'requested'->'episodeIds' ? e."Id"::text
                        UNION ALL
                        SELECT e."SeasonNumber" FROM "RequestTargets" t JOIN "WorkEpisodes" e ON e."Id" = t."TargetId" AND e."WorkId" = t."WorkId"
                        WHERE "Kind" = 'tv' AND t."RequestId" = source."Id"
                    ) selected ORDER BY number
                ) AS seasons
            FROM source
        )
        """;

    private const string QueueNarrowing = """
        (cardinality(@filterKinds::text[]) = 0 OR "Kind" = ANY(@filterKinds))
        AND (cardinality(@languages::text[]) = 0 OR language = ANY(@languages))
        AND (cardinality(@requesters::text[]) = 0 OR "RequestedByProfileId" = ANY(@requesters))
        AND (@season::integer IS NULL OR @season = ANY(seasons))
        AND (@search::text IS NULL OR strpos(lower("Title"), @search) > 0 OR strpos(lower(COALESCE("Subtitle", '')), @search) > 0
            OR "RequestedByProfileId" = ANY(@matchedNames))
        """;

    public async Task<AdminRequestQueue> ReadQueueAsync(AdminRequestFilter filter, IReadOnlyCollection<MediaAcquisitionKind> kinds, IReadOnlyDictionary<string, string> names, CancellationToken cancellationToken)
    {
        filter = filter with { PageSize = AdminRequestQuery.NormalizePageSize(filter.PageSize), Sort = AdminRequestQuery.NormalizeSort(filter.Sort), Season = filter.Season is >= 0 and <= 99 ? filter.Season : null };
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var statuses = await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"{QueueSource} SELECT \"Status\", COUNT(*) FROM queue WHERE {QueueNarrowing} GROUP BY \"Status\";";
            BindQueue(command, filter, kinds, names);
            var counts = new Dictionary<AcquisitionRequestStatus, int>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                counts[AcquisitionAccessNames.ParseStatus(reader.GetString(0))] = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            }

            return counts;
        }, cancellationToken);
        var tabCounts = Enum.GetValues<AdminRequestTab>().ToDictionary(tab => tab, tab => statuses.Where(pair => tab == AdminRequestTab.All || AdminRequestQuery.TabOf(pair.Key) == tab).Sum(pair => pair.Value));
        var total = statuses.Where(pair => MatchesQueueStatus(pair.Key, filter)).Sum(pair => pair.Value);
        var pageCount = Math.Max(1, (total + filter.PageSize - 1) / filter.PageSize);
        filter = filter with { Page = Math.Clamp(filter.Page, 1, pageCount) };
        var facets = await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                {QueueSource}
                SELECT EXISTS(SELECT 1 FROM source),
                    ARRAY(SELECT DISTINCT "RequestedByProfileId" FROM source ORDER BY "RequestedByProfileId"),
                    ARRAY(SELECT DISTINCT language FROM queue WHERE language IS NOT NULL ORDER BY language),
                    ARRAY(SELECT DISTINCT unnest(seasons) AS season FROM queue ORDER BY season);
                """;
            BindQueue(command, filter, kinds, names);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return (Any: reader.GetBoolean(0), Requesters: reader.GetFieldValue<string[]>(1), Languages: reader.GetFieldValue<string[]>(2), Seasons: reader.GetFieldValue<int[]>(3));
        }, cancellationToken);
        var order = filter.Sort switch
        {
            "oldest" => "\"CreatedAt\", \"Id\"",
            "modified" => "\"UpdatedAt\" DESC, \"CreatedAt\" DESC, \"Id\"",
            "title" => "lower(\"Title\"), \"Id\"",
            "requester" => "COALESCE((SELECT lower(a.\"UserName\") FROM \"OwnerAccounts\" a WHERE a.\"Id\" = queue.\"RequestedByProfileId\"), lower(\"RequestedByProfileId\")), \"CreatedAt\" DESC, \"Id\"",
            _ => "\"CreatedAt\" DESC, \"Id\""
        };
        var items = await QueryAsync($"""
            {QueueSource}
            SELECT {Columns} FROM queue WHERE {QueueNarrowing} AND "Status" = ANY(@statuses)
            ORDER BY {order} LIMIT @limit OFFSET @offset;
            """, command =>
        {
            BindQueue(command, filter, kinds, names);
            Add(command, "@statuses", Enum.GetValues<AcquisitionRequestStatus>().Where(status => MatchesQueueStatus(status, filter)).Select(AcquisitionAccessNames.Status).ToArray());
            Add(command, "@limit", filter.PageSize);
            Add(command, "@offset", (filter.Page - 1) * filter.PageSize);
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var languages = facets.Languages.OrderBy(language => RequestLanguages.Choices.ToList().FindIndex(choice => choice.Tag == language)).ThenBy(language => language).ToArray();
        return new AdminRequestQueue(new AdminRequestPage(items, filter, tabCounts, languages, facets.Seasons, total, pageCount), facets.Any, facets.Requesters);
    }

    private static bool MatchesQueueStatus(AcquisitionRequestStatus status, AdminRequestFilter filter) =>
        (filter.Tab == AdminRequestTab.All || AdminRequestQuery.TabOf(status) == filter.Tab)
        && (filter.Statuses is not { Count: > 0 } || filter.Statuses.Contains(status));

    private static void BindQueue(DbCommand command, AdminRequestFilter filter, IReadOnlyCollection<MediaAcquisitionKind> kinds, IReadOnlyDictionary<string, string> names)
    {
        Add(command, "@kinds", kinds.Select(AcquisitionAccessNames.Kind).ToArray());
        Add(command, "@filterKinds", (filter.Kinds ?? []).Select(AcquisitionAccessNames.Kind).Distinct().ToArray());
        Add(command, "@languages", (filter.Languages ?? []).Distinct().ToArray());
        Add(command, "@requesters", (filter.RequesterProfileIds ?? []).Distinct().ToArray());
        Add(command, "@season", filter.Season);
        Add(command, "@search", filter.Search?.Trim().ToLowerInvariant());
        Add(command, "@matchedNames", names.Where(pair => filter.Search is not null && pair.Value.Contains(filter.Search.Trim(), StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray());
    }
}
