using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.OpenLibrary;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // What one row of the list is and what would happen to it.
    internal sealed record MediaImportPlanRow(MediaImportItem Item, MediaImportStatusType Status, string? Problem, string? ProblemDetail, long? WorkId, string? MatchTitle, int? MatchYear,
        string? MatchSource, MediaImportId? Import, bool HasTarget, bool CanMonitor, IReadOnlyList<MediaImportCandidateV1> Candidates, bool IsSearched, string? Query, int? DuplicateOf, bool IsIncluded,
        MediaImportActionType Action, long? AcquisitionProfileId, long? LibraryRootId);

    internal sealed record MediaImportPlan(IReadOnlyList<MediaImportPlanRow> Rows, int Unsearched, string? ProviderFailure);

    // Matches a list to the library and the providers without changing anything: reliable identities first, searches only propose candidates.
    internal sealed class MediaImportPlanner(MetadataCatalog catalog, WorkMetadata metadata, BookMetadata books, IMemoryCache cache)
    {
        private const int CandidatesPerRow = 8;
        private const int LookupsPerPlan = 100;
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

        // Provider entries a title of each kind can be added from, the first being the preferred one.
        private static string[] ImportNamespaces(MediaType type, MediaSubtype? subtype) => (type, subtype) switch
        {
            (MediaType.Movie, _) => ["tmdb_movie"],
            (MediaType.Series, MediaSubtype.Anime) => ["tmdb_tv", "anilist"],
            (MediaType.Series, _) => ["tmdb_tv"],
            (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel) => ["anilist", "openlibrary"],
            (MediaType.Book, _) => ["openlibrary"],
            (MediaType.Music, _) => ["musicbrainz"],
            _ => ["igdb"]
        };

        public async Task<MediaImportPlan> PlanAsync(LogicContext context, IReadOnlyList<MediaImportItem> items, MediaImportDataV1 data, int searches, CancellationToken ct, Func<int, string, int?, string?, string?, string?, Task>? onProgressAsync = null)
        {
            var choices = data.Choices.ToDictionary(choice => choice.Index);
            if (choices.Keys.Any(index => index >= items.Count)) throw new DataValidationException(nameof(data.Choices), "unknown_reference");
            var defaults = data.Defaults.ToDictionary(item => item.Key, StringComparer.Ordinal);

            // The identities a row names, and the provider entry a person chose for it.
            var identities = new List<MediaImportId>[items.Count];
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                identities[index] = [.. item.Ids];
                if (item.Problem is null && choices.GetValueOrDefault(index)?.Choice is { } choice)
                {
                    var chosen = ReadChoice(item, choice);
                    identities[index].RemoveAll(identity => identity.Namespace == chosen.Namespace);
                    identities[index].Add(chosen);
                }
            }

            var known = await ReadKnownAsync(context, identities.SelectMany(list => list), ct);
            // Large imports finish title/author matching in the server-owned job.
            // Do not block their preview on hundreds of per-ISBN provider calls.
            var lookups = new Budget(items.Count >= 100 ? 8 : LookupsPerPlan);
            var conflicts = new HashSet<int>();
            var resolvedAny = false;
            string? providerFailure = null;

            // An edition ISBN can identify a Work even when this Jularr instance has not imported it yet.
            // Provider metadata is checked against the requested title/author before using that Work id.
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.Problem is not null || item.Type != MediaType.Book || Hits(identities[index], known).Count > 0
                    || identities[index].FirstOrDefault(identity => identity.Namespace == "isbn") is not { } isbn)
                {
                    continue;
                }

                if (onProgressAsync is not null) await onProgressAsync(index, "isbn", null, "openlibrary", isbn.Value, null);
                var key = "import.isbn." + isbn.Value;
                if (!cache.TryGetValue(key, out OpenLibraryWork? book))
                {
                    if (!lookups.TrySpend()) break;
                    try
                    {
                        book = await books.ReadWorkForIsbnAsync(isbn.Value, ct);
                    }
                    catch (KeyNotFoundException)
                    {
                        book = null;
                    }
                    catch (ServiceUnavailableException exception)
                    {
                        providerFailure ??= ProviderFailureKey(exception);
                        lookups.Stop();
                        break;
                    }

                    cache.Set(key, book, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 });
                }

                if (book is null || !IsSameBook(item, book)) continue;
                var other = identities[index].FirstOrDefault(identity => identity.Namespace == "openlibrary");
                if (other is not null && other.Value != book.WorkId)
                {
                    conflicts.Add(index);
                    continue;
                }

                if (other is null)
                {
                    identities[index].Add(new MediaImportId("openlibrary", book.WorkId));
                    resolvedAny = true;
                }
            }

            // The ISBN-resolved Work may already exist with another ISBN in the local database.
            if (resolvedAny) known = await ReadKnownAsync(context, identities.SelectMany(list => list), ct);

            // Video titles named only by IMDb or TVDB find their TMDB entry.
            var budget = new Budget(searches);
            var found = new Dictionary<int, MediaImportId>();
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.Problem is not null || Hits(identities[index], known).Count > 0 || Preferred(item, identities[index]) is not null) continue;
                if (await FindTmdbAsync(context, item, identities[index], lookups, ct) is { } tmdb)
                {
                    found[index] = tmdb;
                    identities[index].Add(tmdb);
                }
            }

            if (found.Count > 0) known = [.. known, .. await ReadKnownAsync(context, found.Values, ct)];
            var excluded = await ReadExcludedAsync(context, identities.SelectMany(list => list), ct);

            // Open rows with a title or a search of a person's own ask the provider; their own searches come first.
            var candidates = new Dictionary<int, IReadOnlyList<MediaImportCandidateV1>>();
            var open = Enumerable.Range(0, items.Count).Where(index => items[index].Problem is null && Hits(identities[index], known).Count == 0 && Preferred(items[index], identities[index]) is null
                    && (items[index].Title is not null || QueryOf(choices.GetValueOrDefault(index)) is not null))
                .OrderBy(index => QueryOf(choices.GetValueOrDefault(index)) is null).ThenBy(index => index).ToArray();
            foreach (var index in open)
            {
                var item = items[index];
                var query = QueryOf(choices.GetValueOrDefault(index));
                var actualQuery = query ?? (item.Type == MediaType.Book && item.Creator is not null
                    ? item.Title + " " + item.Creator : item.Title!);
                var source = item.Type == MediaType.Book ? "openlibrary" : "catalog";
                if (onProgressAsync is not null) await onProgressAsync(index, "searching", null, source, actualQuery, null);
                var results = await SearchAsync(context, item, query, budget, ct,
                    error => providerFailure ??= ProviderFailureKey(error));
                if (results is not null) candidates[index] = results;
                if (onProgressAsync is not null) await onProgressAsync(index,
                    results is null ? "waiting" : results.Count > 0 ? "candidates" : "no_match",
                    results?.Count, source, actualQuery, results is null ? providerFailure ?? "search_budget" : null);
            }

            var rows = new List<MediaImportPlanRow>(items.Count);
            var firstOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var choice = choices.GetValueOrDefault(index);
                candidates.TryGetValue(index, out var list);
                var canMonitor = item.Type is not null and not MediaType.Game;
                if (item.Problem is not null)
                {
                    rows.Add(Row(item, MediaImportStatusType.Invalid, item.Problem, item.ProblemDetail, canMonitor, list, choice));
                    continue;
                }

                var hits = Hits(identities[index], known);
                MediaImportPlanRow row;
                if (conflicts.Contains(index) || hits.Count > 1)
                {
                    row = Row(item, MediaImportStatusType.Invalid, "ids_conflict", null, canMonitor, list, choice);
                }
                else if (hits.Count == 1)
                {
                    var hit = hits[0];
                    row = hit.MediaTypeId != item.Type
                        ? Row(item, MediaImportStatusType.Invalid, "type_mismatch", item.TypeKey, canMonitor, list, choice)
                        : Row(item, MediaImportStatusType.InLibrary, null, null, canMonitor, list, choice) with
                        {
                            WorkId = hit.WorkId,
                            MatchTitle = hit.Title,
                            MatchYear = hit.ReleaseYear,
                            MatchSource = hit.Namespace,
                            HasTarget = hit.HasTarget
                        };
                }
                else if (Preferred(item, identities[index]) is { } import)
                {
                    var label = list?.FirstOrDefault(candidate => candidate.Choice == import.Namespace + ":" + import.Value);
                    row = excluded.Any(identity => identities[index].Any(own => own.Namespace == identity.Namespace && own.Value == identity.Value))
                        ? Row(item, MediaImportStatusType.Invalid, "excluded", null, canMonitor, list, choice)
                        : Row(item, MediaImportStatusType.New, null, null, canMonitor, list, choice) with
                        {
                            Import = import,
                            MatchTitle = label?.Title ?? item.Title,
                            MatchYear = label?.ReleaseYear ?? item.Year,
                            MatchSource = import.Namespace
                        };
                }
                else
                {
                    row = Row(item, MediaImportStatusType.Open, identities[index].Count > 0 ? "no_match" : null, null, canMonitor, list, choice);
                }

                // The first row of the file that names a title keeps it.
                var key = row.WorkId is { } workId ? "work:" + workId.ToString(CultureInfo.InvariantCulture) : row.Import is { } entry ? entry.Namespace + ":" + entry.Value : null;
                if (key is not null && row.Status is MediaImportStatusType.New or MediaImportStatusType.InLibrary)
                {
                    if (firstOf.TryGetValue(key, out var first)) row = row with { Status = MediaImportStatusType.Duplicate, DuplicateOf = first };
                    else firstOf[key] = index;
                }

                row = Decide(row, choice, data, defaults);
                rows.Add(row);
                if (onProgressAsync is not null) await onProgressAsync(index, row.Status.ToString(), row.Candidates.Count, null, null, row.Problem);
            }

            var unsearched = rows.Count(row => row.Status == MediaImportStatusType.Open && !row.IsSearched && (row.Item.Title is not null || row.Query is not null));
            return new MediaImportPlan(rows, unsearched, providerFailure);
        }

        private static string ProviderFailureKey(ServiceUnavailableException exception) =>
            exception.Message == "metadata_rate_limited" ? "rate_limited" : "unavailable";

        private static string? QueryOf(MediaImportChoiceV1? choice) => choice?.Query is { } query && !string.IsNullOrWhiteSpace(query) ? query : null;

        // A row that was not searched has no candidates yet; one that was and found none has an empty list.
        private static MediaImportPlanRow Row(MediaImportItem item, MediaImportStatusType status, string? problem, string? detail, bool canMonitor, IReadOnlyList<MediaImportCandidateV1>? candidates,
            MediaImportChoiceV1? choice) => new(item, status, problem, detail, null, null, null, null, null, false, canMonitor, candidates ?? [], candidates is not null, QueryOf(choice), null, false,
            MediaImportActionType.AddOnly, null, null);

        // Who is included and what happens to each: the person's choice, else the list's, and only what can really happen.
        private static MediaImportPlanRow Decide(MediaImportPlanRow row, MediaImportChoiceV1? choice, MediaImportDataV1 data, Dictionary<string, MediaImportDefaultV1> defaults)
        {
            var action = row.CanMonitor ? choice?.Action ?? data.Action : MediaImportActionType.AddOnly;
            var kind = defaults.GetValueOrDefault(row.Item.TypeKey);
            var profile = choice?.AcquisitionProfileId ?? kind?.AcquisitionProfileId;
            var library = choice?.LibraryRootId ?? kind?.LibraryRootId;

            // Something is left to do for a new title, and for one of the library that is to be monitored and is not yet.
            var actionable = row.Status == MediaImportStatusType.New || row.Status == MediaImportStatusType.InLibrary && !row.HasTarget && action != MediaImportActionType.AddOnly;
            var included = actionable && (choice?.Include ?? true);
            return row with { Action = action, AcquisitionProfileId = profile, LibraryRootId = library, IsIncluded = included };
        }

        // The entries of the library that the identities of one row name.
        private static List<MediaImportKnownData> Hits(IReadOnlyList<MediaImportId> identities, IReadOnlyList<MediaImportKnownData> known)
        {
            var wanted = identities.SelectMany(Variants).ToHashSet();
            return [.. known.Where(entry => wanted.Contains(new MediaImportId(entry.Namespace, entry.Value))).DistinctBy(entry => entry.WorkId)];
        }

        // A library may hold an ISBN in its ten-digit form.
        private static IEnumerable<MediaImportId> Variants(MediaImportId identity)
        {
            yield return identity;
            if (identity.Namespace == "isbn" && MediaImportParser.Isbn10(identity.Value) is { } ten) yield return identity with { Value = ten };
        }

        // The provider entry a title is added from.
        private static MediaImportId? Preferred(MediaImportItem item, IReadOnlyList<MediaImportId> identities) =>
            ImportNamespaces(item.Type!.Value, item.Subtype).Select(@namespace => identities.FirstOrDefault(identity => identity.Namespace == @namespace)).FirstOrDefault(identity => identity is not null);

        // "namespace:id" of a provider entry that this kind of title can be added from.
        private static MediaImportId ReadChoice(MediaImportItem item, string choice)
        {
            var colon = choice.IndexOf(':');
            if (colon <= 0 || item.Type is not { } type) throw new DataValidationException("Choices", "invalid_value");
            var @namespace = choice[..colon];
            var identity = ImportNamespaces(type, item.Subtype).Contains(@namespace, StringComparer.Ordinal)
                ? MediaImportParser.Identity(MediaImportParser.Kind(@namespace), choice[(colon + 1)..], type, item.Subtype, out _) : null;
            return identity is not null && identity.Namespace == @namespace ? identity : throw new DataValidationException("Choices", "invalid_value");
        }

        private async Task<MediaImportId?> FindTmdbAsync(LogicContext context, MediaImportItem item, IReadOnlyList<MediaImportId> identities, Budget budget, CancellationToken ct)
        {
            if (item.Type is not (MediaType.Movie or MediaType.Series)) return null;
            foreach (var identity in identities.Where(identity => identity.Namespace is "imdb" or "tvdb"))
            {
                var key = $"import.find.{item.Type}.{identity.Namespace}.{identity.Value}";
                if (!cache.TryGetValue(key, out int? id))
                {
                    if (!budget.TrySpend()) return null;
                    try
                    {
                        id = await metadata.FindExternalAsync(await metadata.ConnectAsync(context, ct), item.Type.Value, identity.Namespace, identity.Value, ct);
                    }
                    catch (ServiceUnavailableException)
                    {
                        budget.Stop();
                        return null;
                    }

                    cache.Set(key, id, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 });
                }

                if (id is { } tmdb) return new MediaImportId(WorkMetadata.Namespace(item.Type.Value), tmdb.ToString(CultureInfo.InvariantCulture));
            }

            return null;
        }

        // Candidates for an open row; nothing when the budget of this preview is spent or the provider is out of reach.
        private async Task<IReadOnlyList<MediaImportCandidateV1>?> SearchAsync(LogicContext context, MediaImportItem item, string? query, Budget budget, CancellationToken ct,
            Action<ServiceUnavailableException>? onUnavailable = null)
        {
            var type = item.Type!.Value;
            var text = query ?? (type == MediaType.Book && item.Creator is not null ? item.Title + " " + item.Creator : item.Title!);
            var key = $"import.search.{type}.{item.Subtype}.{text.ToLowerInvariant()}";
            if (!cache.TryGetValue(key, out IReadOnlyList<MetadataSearchResultV1>? results))
            {
                if (!budget.TrySpend()) return null;
                try
                {
                    results = await catalog.SearchAsync(context, type, item.Subtype, text, null, ct);
                }
                catch (ServiceUnavailableException exception)
                {
                    onUnavailable?.Invoke(exception);
                    budget.Stop();
                    return null;
                }

                cache.Set(key, results, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = Math.Max(1, results.Count) });
            }

            var allowed = ImportNamespaces(type, item.Subtype);
            var shown = results!.Where(result => allowed.Contains(result.Namespace, StringComparer.Ordinal))
                .Where(result => !IsRelatedBookProduct(item, result))
                .OrderByDescending(result => CandidateScore(item, result))
                .Take(CandidatesPerRow).ToArray();

            // One candidate that carries the whole title and what the file says of year or author is proposed, never taken.
            var strong = query is not null ? [] : shown.Where(result => IsStrong(item, result)).ToArray();
            return [.. shown.Select(result => new MediaImportCandidateV1(result.Namespace + ":" + result.ExternalId, result.Title, result.ReleaseYear, TextLimit.Cut(result.Author, 500), result.Namespace,
                result.WorkId, strong.Length == 1 && ReferenceEquals(strong[0], result)))];
        }

        // An exact title and matching creator outrank popular but unrelated editions.
        // Never turn a title-only score into an automatic import.
        private static int CandidateScore(MediaImportItem item, MetadataSearchResultV1 result)
        {
            var score = 0;
            if (item.Title is { } title)
            {
                var requested = TitleMatcher.SignificantWords(title);
                var actual = TitleMatcher.SignificantWords(result.Title);
                if (requested.Count > 0)
                {
                    score += requested.SetEquals(actual) ? 100 : 60 * requested.Intersect(actual).Count() / requested.Count;
                }
            }

            if (item.Creator is { } creator && result.Author?.Contains(creator, StringComparison.OrdinalIgnoreCase) == true) score += 40;
            if (item.Year is { } year && result.ReleaseYear == year) score += 10;
            if (result.WorkId is not null) score += 5;
            return score;
        }

        // Distinct companion products must not masquerade as the requested full book.
        // A requested summary/workbook remains legitimate when that is its actual title.
        private static bool IsRelatedBookProduct(MediaImportItem item, MetadataSearchResultV1 result)
        {
            if (item.Type != MediaType.Book || item.Subtype is MediaSubtype.Manga or MediaSubtype.LightNovel) return false;
            var wanted = item.Title?.Trim() ?? "";
            var title = result.Title.Trim();
            var prefixes = new[] { "summary of ", "summary: ", "analysis of ", "review of ", "study guide for ", "study guide to ", "workbook for ", "workbook of " };
            if (prefixes.Any(prefix => title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !wanted.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
            var suffixes = new[] { " study guide", " workbook", " discussion journal", " summary", " (audiobook)", " - audiobook", " (hörbuch)", " - hörbuch" };
            return suffixes.Any(suffix => title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && !wanted.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        // An ISBN match must not quietly turn a full book into a summary, workbook or a different author's title.
        private static bool IsSameBook(MediaImportItem item, OpenLibraryWork work)
        {
            if (item.Title is { } title)
            {
                var requested = TitleMatcher.SignificantWords(title);
                var found = TitleMatcher.SignificantWords(work.Title);
                if (found.Count == 0 || !(found.SetEquals(requested)
                    || found.IsSubsetOf(requested) && requested.Except(found).All(word => word is "revised" or "updated" or "edition" or "expanded" or "unabridged")))
                {
                    return false;
                }
            }

            if (item.Creator is not { } creator) return true;
            var wanted = creator.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return wanted.Length > 0 && work.Authors.Any(author =>
            {
                var actual = author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return actual.Length > 0 && wanted[0].Equals(actual[0], StringComparison.OrdinalIgnoreCase)
                    && wanted[^1].Equals(actual[^1], StringComparison.OrdinalIgnoreCase);
            });
        }

        private static bool IsStrong(MediaImportItem item, MetadataSearchResultV1 result)
        {
            if (item.Title is null || item.Year is null && item.Creator is null) return false;
            var wanted = TitleMatcher.SignificantWords(item.Title);
            if (wanted.Count == 0 || !wanted.SetEquals(TitleMatcher.SignificantWords(result.Title))) return false;
            if (item.Year is { } year && result.ReleaseYear != year) return false;
            return item.Creator is null || result.Author?.Contains(item.Creator, StringComparison.OrdinalIgnoreCase) == true;
        }

        private static Task<IReadOnlyList<MediaImportKnownData>> ReadKnownAsync(LogicContext context, IEnumerable<MediaImportId> identities, CancellationToken ct)
        {
            var lookup = identities.SelectMany(Variants).Distinct().ToArray();
            return lookup.Length == 0 ? Task.FromResult<IReadOnlyList<MediaImportKnownData>>([]) : context.ExecuteBatchAsync((ctx, token) => MediaImportQueries.ReadKnownAsync(ctx, Lookup(lookup), token), ct);
        }

        private static Task<IReadOnlyList<ExclusionIdentityData>> ReadExcludedAsync(LogicContext context, IEnumerable<MediaImportId> identities, CancellationToken ct)
        {
            var lookup = identities.Distinct().ToArray();
            return lookup.Length == 0 ? Task.FromResult<IReadOnlyList<ExclusionIdentityData>>([]) : context.ExecuteBatchAsync((ctx, token) => MediaImportQueries.ReadExcludedAsync(ctx, Lookup(lookup), token), ct);
        }

        private static WorkIdentityLookupData Lookup(IEnumerable<MediaImportId> identities) =>
            new(JsonSerializer.Serialize(identities.Select(identity => new { identity.Namespace, identity.Value })));

        // The provider calls one preview may make.
        private sealed class Budget(int remaining)
        {
            public bool TrySpend()
            {
                if (remaining <= 0) return false;
                remaining--;
                return true;
            }

            // A provider that does not answer ends the asking.
            public void Stop() => remaining = 0;
        }
    }

    internal static class MediaImportQueries
    {
        // Titles of the library that identities name: Works, their editions and their seasons.
        public static Task<IReadOnlyList<MediaImportKnownData>> ReadKnownAsync(LogicContext ctx, WorkIdentityLookupData lookup, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    found."Namespace",
                    found."Value",
                    work."Id" AS "WorkId",
                    work."MediaTypeId",
                    work."CanonicalTitle" AS "Title",
                    work."ReleaseYear",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "WantedItems" AS wanted
                        WHERE
                            wanted."WorkId" = work."Id"
                    ) AS "HasTarget"
                FROM
                    (
                        SELECT
                            identity."Namespace",
                            identity."Value",
                            identity."WorkId"
                        FROM
                            "WorkExternalIdentities" AS identity
                            JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value"
                        UNION
                        SELECT
                            identity."Namespace",
                            identity."Value",
                            edition."WorkId"
                        FROM
                            "WorkEditionExternalIdentities" AS identity
                            JOIN "WorkEditions" AS edition ON edition."Id" = identity."WorkEditionId"
                            JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value"
                        UNION
                        SELECT
                            identity."Namespace",
                            identity."Value",
                            unit."WorkId"
                        FROM
                            "WorkUnitExternalIdentities" AS identity
                            JOIN "WorkUnits" AS unit ON unit."Id" = identity."WorkUnitId"
                            JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value"
                    ) AS found
                    JOIN "Works" AS work ON work."Id" = found."WorkId";
                """;
            sql.Parameters.Add(lookup);
            return ctx.ReadSql.ReadListAsync<MediaImportKnownData>(sql, ct);
        }

        // Identities of titles that were deleted with an exclusion.
        public static Task<IReadOnlyList<ExclusionIdentityData>> ReadExcludedAsync(LogicContext ctx, WorkIdentityLookupData lookup, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."Namespace",
                    identity."Value"
                FROM
                    "WorkExclusionIdentities" AS identity
                    JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value";
                """;
            sql.Parameters.Add(lookup);
            return ctx.ReadSql.ReadListAsync<ExclusionIdentityData>(sql, ct);
        }

        // The titles the library gives its Works.
        public static Task<IReadOnlyList<MediaImportWorkData>> ReadTitlesAsync(LogicContext ctx, MediaImportWorkLookupData lookup, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."CanonicalTitle" AS "Title"
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = ANY(@WorkIds);
                """;
            sql.Parameters.Add(lookup);
            return ctx.ReadSql.ReadListAsync<MediaImportWorkData>(sql, ct);
        }

        public static Task<IReadOnlyList<MediaImportOptionV1>> ReadProfilesAsync(LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."Name",
                    profile."MediaTypeId",
                    profile."MediaSubtypeId"
                FROM
                    "AcquisitionProfiles" AS profile
                ORDER BY
                    profile."Name",
                    profile."Id"
                LIMIT 200;
                """;
            return ctx.ReadSql.ReadListAsync<MediaImportOptionV1>(sql, ct);
        }

        public static Task<IReadOnlyList<MediaImportOptionV1>> ReadLibrariesAsync(LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    root."Id",
                    root."Name",
                    root."MediaTypeId",
                    root."MediaSubtypeId"
                FROM
                    "LibraryRoots" AS root
                ORDER BY
                    root."Name",
                    root."Id"
                LIMIT 200;
                """;
            return ctx.ReadSql.ReadListAsync<MediaImportOptionV1>(sql, ct);
        }
    }
}
