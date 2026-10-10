import fs from 'node:fs';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';

const [catalogPath, auditPath, prPath, workspacePath] = process.argv.slice(2);
if (!catalogPath || !auditPath || !prPath || !workspacePath) throw new Error('Usage: node scripts/cutover/reconcile.mjs <catalog JSON> <Roslyn JSON> <PR JSON> <worktree JSON>');
import { read, json, csvRead, csvWrite, writeJson } from './inventory-io.mjs';
const unique = values => [...new Set(values.filter(Boolean))].sort();
const join = values => unique(values).join('; ');
const sha = execFileSync('git', ['rev-parse', 'origin/dev'], { encoding: 'utf8' }).trim();
assert.equal(execFileSync('git', ['diff', '--name-only', 'origin/dev', '--', 'src', 'clients', 'tests', 'compose.yaml', 'Dockerfile'], { encoding:'utf8' }).trim(), '', 'Audited product source differs from origin/dev');
const capturedAt = new Date().toISOString();
const catalog = json(catalogPath), audit = json(auditPath), prs = json(prPath), workspaces = json(workspacePath);
const catalogCapturedAt = catalog.capturedAt ?? fs.statSync(catalogPath).mtime.toISOString();
assert.equal(catalog.readOnly, 'on');
const tableFile = 'docs/DATABASE_CUTOVER_INVENTORY_TABLES.csv';
const tables = csvRead(tableFile).filter(row => row.RowKind !== 'TARGET_ONLY');
const live = catalog.relations.filter(row => ['r', 'p'].includes(row.kind));
const tableNames = new Set(tables.map(row => row.CurrentTable));
for (const relation of live) {
    if (!tableNames.has(relation.name)) tables.push({ CurrentTable: relation.name, ClrEntity: 'LIVE_CATALOG_NO_EF_ENTITY', Evidence: 'docs/_phase_a_raw_schema_0.json' });
}
const methods = new Map(audit.methods.map(row => [row.symbol, row]));
const callers = new Map();
for (const edge of audit.edges) {
    if (!callers.has(edge.callee)) callers.set(edge.callee, new Set());
    callers.get(edge.callee).add(edge.caller);
}
const rootKinds = method => !method ? '' : /\/Pages\//.test(method.path) && /\.On(Get|Post|Put|Delete|Patch)/.test(method.symbol) ? 'RAZOR_HANDLER'
    : /Endpoints\.cs$/.test(method.path) && /\.Map/.test(method.symbol) ? 'HTTP_REGISTRATION_WITH_CALLBACKS'
    : /\.ExecuteAsync\(/.test(method.symbol) ? 'WORKER'
    : method.path.endsWith('/Program.cs') ? 'STARTUP_OR_AUTH_MIDDLEWARE' : '';
const rootCache = new Map();
function roots(symbol) {
    if (rootCache.has(symbol)) return rootCache.get(symbol);
    const pending = [symbol], visited = new Set(), found = [];
    while (pending.length) {
        const current = pending.pop(); if (visited.has(current)) continue; visited.add(current);
        const method = methods.get(current);
        if (rootKinds(method)) found.push(`${rootKinds(method)}:${current}`);
        else for (const caller of callers.get(current) ?? []) pending.push(caller);
    }
    const result = unique(found); rootCache.set(symbol,result); return result;
}
function domainFor(table) {
    if (/^(Account|OwnerAccount|PlexLogin)/.test(table)) return 'Accounts';
    if (/^(ProfileWatchlist|ProfileFranchise|Watchlist)/.test(table)) return 'Watchlist';
    if (/^(UiTranslation|UiLocale)/.test(table)) return 'Localization';
    if (/^(Profiles|ProfileRatings|UiProfile|ProfileHome|ProfilePlayback|ReaderPreferences|ChapterArtworkPreferences|KnownDevices)/.test(table)) return 'Profiles';
    if (/^(Subtitle)/.test(table)) return 'Subtitles';
    if (/^Music/.test(table)) return 'Music';
    if (/^(EditionExternalIdentities)/.test(table)) return 'Works';
    if (/^(Game|StoredFileHashes)/.test(table)) return 'Games';
    if (/^(Learning|Learner|SharedCourse|Curriculum|Term|EpisodeTerm)/.test(table)) return 'Learning';
    if (/^(Ai|ImageGeneration)/.test(table)) return 'Ai';
    if (/^(ChapterArtwork|MediaArtwork|WorkArtwork|Image)/.test(table)) return 'Images';
    if (/^(EpisodeMediaSegments|EpisodeSegmentDetection|MediaSegment|MediaChapter|MediaDetection)/.test(table)) return 'MediaDetection';
    if (/^(MediaProgress|EpisodeProgress|NovelProgress|MangaProgress|AudiobookProgress|.*ProgressPositions|ProgressPosition|.*PlaybackHistory|ActiveSessions|PlaybackSessions)/.test(table)) return 'Progress';
    if (/^(Acquisition|Wanted|Request|WorkMonitoring)/.test(table)) return 'Acquisition';
    if (/^(Operation|Event|Notification)/.test(table)) return 'Operations';
    if (/^(ReleaseCalendar)/.test(table)) return 'Calendar';
    if (/^(Library|StoredFile|MediaFiles|MediaAsset|MediaTrack|MediaTechnical|MediaAnalys)/.test(table)) return 'Library';
    if (/^(Novel|Manga|Book|Audiobook|Reader)/.test(table)) return 'Reader';
    if (/^(Instance|Discovery|Presentation)/.test(table)) return 'Instance';
    if (/^(MediaTypes|Franchise|ProfileFranchise|MediaRelations|ProviderRole|Work|Anime|Movie|TvSeries|Collection)/.test(table)) return 'Works';
    return 'REQUIRES_DOMAIN_REVIEW';
}
const dispositions = new Map();
function decide(names, action, targets, reason, fields, owner) {
    for (const name of names.split(' ')) dispositions.set(name, { action, targets: targets.split(' ').filter(Boolean), reason, fields, owner });
}
decide('OwnerAccounts', 'SPLIT', 'Accounts Profiles AccountProfiles AccountPasswords AccountRoleTypes', 'CLEAN_CUT_DATABASE §1/§3: sign-in identity and personal profile separate; current OwnerAccount anchors both.', 'Id -> bigint Account; UserName/NormalizedUserName -> required verified Email login; PasswordHash -> AccountPasswords; Role -> AccountRoleTypes; personal references -> Profiles.', 'Accounts');
decide('AccountLoginIdentities', 'RENAME', 'AccountExternalLogins', '§4 replaces provider sign-in identity; distinct from profile service connections.', 'AccountId -> Accounts.Id bigint; provider/external identity uniqueness retained.', 'Accounts');
decide('AccountSessionStates', 'MERGE', 'AccountSessions', '§4 removes duplicate session-version bookkeeping; §3 durable hashed sign-in sessions/revocation.', 'AccountId -> Accounts; Version revocation semantics -> AccountSessions lifecycle; exact design in B.', 'Accounts');
decide('Anime Movies TvSeries NovelWorks MangaSeries Games', 'MERGE', 'Works WorkTitles WorkExternalIdentities WorkMetadataFacts WorkLocalizedValues WorkMediaClassifications ImageAssignments', '§1/§4 replace competing media roots; Anime is classification, Game is Work media type.', 'Per-module root/provider IDs -> Works.Id/WorkExternalIdentities; titles/facts/artwork -> shared owners; paths never Work identity.', 'Works');
decide('Audiobooks', 'SPLIT', 'Works WorkEditions WorkVersions MediaAssets WorkCredits', '§3 audiobook is Book Work plus audio edition/assets/credits; distinct adaptation is explicit WorkRelation.', 'Root -> Book Work; edition/file/credit concerns split; genuinely distinct adaptation resolved before B.', 'Works');
decide('Episodes', 'SPLIT', 'WorkEpisodes WorkSeasons MediaAssets StoredFiles', '§1 logical episode separate from physical media; §4 removes old root.', 'AnimeId -> Work; episode/season -> logical units; files -> shared asset/file.', 'Works');
decide('NovelVolumes MangaVolumes', 'MERGE', 'WorkVolumes', '§3 canonical logical reading volume.', 'Work/Series -> Works; number/title/provider evidence -> canonical volume.', 'Reader');
decide('NovelChapters MangaChapters', 'SPLIT', 'WorkChapters ReaderContent ReaderPages', '§3 logical chapter separate from edition content/navigation.', 'Work/Series/volume -> canonical units; text/page/source -> content/files; preserve fractional Manga ordering.', 'Reader');
decide('MangaPages', 'RENAME', 'ReaderPages', '§3 shared reader page identity.', 'ChapterId -> canonical content/WorkChapter; preserve page index, mime/source entry.', 'Reader');
decide('BookEditions NovelVolumeEditions', 'MERGE', 'WorkEditions WorkVersions', '§3 shared editions and concrete versions.', 'Legacy Work/Volume -> canonical Work/Volume/Edition/Version; preserve source/provider data.', 'Reader');
decide('BookFiles AudiobookFiles', 'SPLIT', 'MediaAssets StoredFiles MediaTracks', '§1/§3 one physical inventory chain.', 'Root/path/bytes -> StoredFiles; logical rendition -> MediaAssets; stream facts -> MediaTracks.', 'Library');
decide('AudiobookEditionMetadata', 'SPLIT', 'WorkEditions EditionExternalIdentities WorkCredits MediaTechnicalAnalyses', '§3/§4 no duplicate audiobook metadata.', 'ISBN/ASIN -> EditionExternalIdentities; narrators/publishers -> edition credits; technical facts -> analysis.', 'Works');
decide('GameTitles', 'MERGE', 'WorkTitles', '§4 shared title owner.', 'GameId -> WorkId; preserve provenance/locale.', 'Works');
decide('GameExternalIdentities', 'MERGE', 'WorkExternalIdentities', '§4 shared provider identity owner.', 'GameId -> WorkId bigint; external provider identity distinct from Id.', 'Works');
decide('GameArtworks MediaArtworkAssets WorkArtwork', 'SPLIT', 'Images ImageAssignments', '§3/§4 one image identity with explicit target/cardinality.', 'Path/hash/mime -> Images; Work/Chapter/Season/kind -> ImageAssignments; no independent GameId.', 'Images');
decide('ChapterArtworks', 'SPLIT', 'ImageGenerationRequests Images ImageAssignments', '§4 request lifecycle distinct from accepted image.', 'Operation/status/prompt/requester -> request; accepted asset -> Images; chapter -> assignment.', 'Images');
decide('GameReleases', 'KEEP', 'GameReleases WorkVersions', '§3 specialized release responsibility, rebuilt as WorkVersionId PK/FK extension.', 'Old Id/GameId -> WorkVersionId PK/FK/canonical Work; preserve platform/revision/region/language/release kind.', 'Games');
decide('GameReleaseFiles', 'SPLIT', 'StoredFiles GameReleaseStoredFiles StoredFileHashes', '§3/§4 shared file identity plus verified multi-disc extension.', 'Root/path/size -> StoredFiles; DiscNumber/Sequence/Role -> release membership; file CRC/MD5/SHA -> StoredFileHashes.', 'Games');
decide('GameReleaseHashes', 'KEEP', 'GameReleaseHashes', 'GameModels.cs: release-level identity/serial evidence distinct from file checksum; §3 permits normalized release evidence.', 'GameReleaseId -> GameRelease.WorkVersionId; retain Algorithm/Value/IsPrimary; do not attach release hash to arbitrary file.', 'Games');
decide('EpisodeProgress NovelProgress MangaProgress AudiobookProgress', 'MERGE', 'MediaProgress TimeProgressPositions ReadingProgressPositions', '§4 removes parallel progress; §3 explicit completion/exact target.', 'Profile -> Profiles; unit/Work -> bigint; video/audio -> Time; reading locator/page -> Reading; preserve retries/completion.', 'Progress');
decide('MediaProgress', 'SPLIT', 'MediaProgress TimeProgressPositions ReadingProgressPositions GameProgressPositions', '§3 one progress row with exactly matching subtype.', 'UUID IDs -> bigint; Profile -> Profiles; PositionMs/DurationMs -> Time; subtype/cardinality design in B.', 'Progress');
decide('EpisodePlaybackHistory', 'MERGE', 'MediaPlaybackHistory', '§4 equivalent shared history replaces legacy history.', 'Episode/Anime -> canonical units/Work; Profile -> Profiles; event timestamps remain history.', 'Progress');
decide('ActiveSessions', 'RENAME', 'PlaybackSessions', '§4 explicit; separate from AccountSessions.', 'Canonical Work/unit/profile/file FKs; justified public handles may remain opaque.', 'Progress');
decide('EpisodeMediaSegments', 'RENAME', 'MediaSegments', '§4 exact-asset timeline owner.', 'Episode -> MediaAsset; seeded type/source; preserve overlapping/multiple/manual markers.', 'MediaDetection');
decide('EpisodeSegmentDetectionStates', 'RENAME', 'MediaDetectionRuns', '§4 detector history including successful no-match.', 'Episode -> exact asset; fingerprint/version/status/retry -> runs.', 'MediaDetection');
decide('NovelBookmarks MangaBookmarks', 'MERGE', 'ReaderBookmarks', '§3 shared stable annotations.', 'Profile -> Profiles; Work/chapter/edition/page/anchor -> canonical reader targets.', 'Reader');
decide('NovelHighlights', 'MERGE', 'ReaderHighlights', '§3 shared annotation owner.', 'Profile/content anchors -> canonical content; preserve version-aware selection.', 'Reader');
decide('NovelBookmarkTombstones', 'KEEP', 'NovelBookmarkTombstones', 'Existing sync deletion ledger has distinct lifecycle; target has no substitute tombstone contract. Preserve capability; review name/anchor in B.', 'Profile -> Profiles; old bookmark IDs reset/generation-scoped before fresh bigint IDs.', 'Reader');
decide('NovelAnimeMappings MediaRelations', 'MERGE', 'WorkRelations', '§3 directed Work edges, distinct from franchise membership.', 'Legacy/provider endpoints -> Works with provenance/confidence/manual/review state.', 'Works');
decide('ProfileWatchlistPreferences', 'RENAME', 'WatchlistEntries', '§4 provider-key pseudo-watchlist removed.', 'Profile -> Profiles; provider IDs resolve Work; follow/ignore behavior needs complete replacement, never discard ignore state silently.', 'Watchlist');
decide('FranchiseMembers', 'RENAME', 'FranchiseWorks', '§4 canonical membership.', 'Member provider/root -> WorkId; franchise -> canonical franchise.', 'Works');
decide('CollectionItems', 'RENAME', 'CollectionWorks', '§3 profile-owned collection references Works.', 'Canonical Collection/Work FKs; retain ordering/member semantics.', 'Works');
decide('UiProfileLocales', 'MERGE', 'Profiles UiLocales', '§4 profile locale on Profiles.UiLocaleId.', 'Profile -> Profiles.Id; locale code -> UiLocales.Id; exact-parent-English fallback.', 'Profiles');
decide('WorkSourceLinks WorkUnitBindings WorkIdMigrationMap', 'DROP', '', '§4 migration-only bridges excluded from fresh baseline. No backfill; replace callers at atomic cutoff, current installation untouched.', 'All responsibilities already canonical Works/units; historical rows not imported.', 'Works');
decide('MediaFiles', 'RENAME', 'StoredFiles', 'Historical name renamed and absent from verified catalog.', 'Historical columns never counted as active schema.', 'Library');
decide('MediaAnalyses', 'RENAME', 'MediaTechnicalAnalyses', 'Historical name renamed and absent from verified catalog.', 'Historical columns never counted as active schema.', 'Library');
decide('MediaAnalysisStreams', 'RENAME', 'MediaTracks', 'Historical name renamed and absent from verified catalog.', 'Historical columns never counted as active schema.', 'Library');
decide('PlexLoginAttempts', 'MERGE', 'AccountAuthChallenges', '§3 bounded provider login/enrollment challenges; #920 adds login/link Purpose to preserve.', 'Account -> Accounts; preserve nonce/expiry/purpose/consumed semantics; external I/O outside SQL transaction.', 'Accounts');
decide('UiTranslations', 'KEEP', 'UiTranslations', '§3 shared catalog retained as newly designed relations.', 'Locale/MessageKey -> UiLocaleId/UiTranslationMessageId bigint; preserve generated/manual/review state.', 'Localization');
decide('UiLocales', 'KEEP', 'UiLocales', '§3 canonical locale identity.', 'Locale natural PK -> bigint Id plus Locale UNIQUE; rebuild profile/translation FKs.', 'Localization');
decide('UiTranslationMessages', 'KEEP', 'UiTranslationMessages', '§3 one shared message catalog.', 'Key PK -> bigint Id plus Key UNIQUE; preserve source/placeholders/review.', 'Localization');
decide('__EFMigrationsHistory', 'KEEP', '__EFMigrationsHistory', 'Framework history for exactly one fresh baseline then forward migrations.', 'Old 56-row history is inventory only; not imported to fresh baseline.', 'DatabaseInstallation');
decide('MusicAlbums MusicArtists MusicRecordings', 'KEEP', 'MusicAlbums MusicArtists MusicRecordings', 'MusicModels/MediaCoreEntities: album is already Work PK/FK extension; artists and recordings are distinct reusable provider identities, not competing Works. Retain responsibility with fresh schema.', 'MusicAlbums.WorkId remains canonical; Artist/Recording identity -> bigint; provider IDs separate; WorkTrack placement shares MusicRecording; artist monitoring remains WorkMonitoring. Review exact specialty shape in B.', 'Music');
decide('SubtitleLanguageProfiles SubtitleLanguageProfileItems SubtitleProfileAssignments', 'KEEP', 'SubtitleLanguageProfiles SubtitleLanguageProfileItems SubtitleProfileAssignments', 'SubtitleLanguageProfileModels/Service: owner-managed prioritized acquisition policy, distinct from personal Profiles. No replacement specified in target.', 'Item/Assignment.ProfileId references SubtitleLanguageProfile, NOT personal Profiles; bigint policy IDs/FKs; language/forced/SDH/priority/cutoff preserved; root/media scope canonical; null scope uniqueness in B.', 'Subtitles');
decide('SubtitleTracks SubtitleCues', 'KEEP', 'SubtitleTracks SubtitleCues', 'Actual subtitle extraction/cue consumers need normalized timed text; technical MediaTracks does not replace cue content.', 'Episode/file anchors -> canonical WorkEpisode/MediaAsset/StoredFile/MediaTrack; timings/language/forced/SDH preserved; bounded cue queries retained.', 'Subtitles');
decide('AnimeMetadata AnimeLocalMetadata', 'MERGE', 'WorkMetadataFacts WorkLocalizedValues WorkFieldProvenance WorkTitles', 'CLEAN_CUT_DATABASE shared facts/title/provenance owner; MetadataModels and actual Anime metadata readers currently hang on legacy AnimeId.', 'AnimeId -> WorkId; canonical facts/provenance/manual edits -> shared facts, localized non-title values -> localized values, original/translated/alternate titles -> WorkTitles. No separate Anime metadata root.', 'Works');
const catalogConstraints = table => catalog.constraints.filter(row => row.table === table);
const accessByTable = new Map();
const accessRows = [];
for (const entry of audit.accesses) {
    if (entry.table === 'UNRESOLVED_ENTITY_TABLE' && entry.symbol.includes('WorkService.RepointOrDropAsync<T>')) {
        entry.table = 'WorkSeasons; WorkEpisodes; WorkVolumes; WorkChapters; WorkEditions; WorkVersions';
        entry.evidence = 'ROSLYN_GENERIC_TEMPLATE_CONCRETE_CALLS_VERIFIED:WorkService.cs:491-501';
    }
    const method = methods.get(entry.symbol);
    const chain = entry.chain ?? '';
    const operation = /\.(Add|AddRange)\s*\(/.test(chain) ? 'CREATE_TRACKED' : /\.(Remove|RemoveRange)\s*\(/.test(chain) ? 'DELETE_TRACKED'
        : /\.ExecuteDeleteAsync\s*\(/.test(chain) ? 'DELETE' : /\.ExecuteUpdateAsync\s*\(/.test(chain) ? 'UPDATE'
        : (method?.writeMarkers?.includes('SaveChangesAsync') || method?.writeMarkers?.includes('SaveChanges')) ? 'TRACKED_REFERENCE_IN_SAVING_METHOD_REVIEW' : 'READ_OR_QUERY_REFERENCE';
    const row = { Path: entry.path, Symbol: entry.symbol, Line: entry.line, Operation: operation, Tables: entry.table, Columns: join(entry.columns ?? []) || 'DYNAMIC_OR_WHOLE_ENTITY_COLUMN_USE_REVIEW', Evidence: entry.evidence,
        DirectCallers: join([...(callers.get(entry.symbol) ?? [])]), EntryPointCandidates: roots(entry.symbol).join('; '), ActorProfileModuleMarkers: join(method?.scopeMarkers ?? []),
        TargetLogic: `Jularr.Logic.${domainFor(entry.table)}`, TargetService: `Jularr.Service.<User|Admin|System>.${domainFor(entry.table)}.V1`, Review: 'STATIC_BINDING_NOT_VERIFIED_RUNTIME_OR_AUTH_CHAIN' };
    accessRows.push(row);
    for (const table of entry.table.split('; ')) { if (!accessByTable.has(table)) accessByTable.set(table, []); accessByTable.get(table).push(row); }
}
for (const site of audit.sqlSites ?? []) {
    const method = methods.get(site.symbol);
    const knownTables = unique([...(method?.sqlTables ?? []), ...audit.accesses.filter(entry => entry.symbol === site.symbol).flatMap(entry => entry.table.split('; '))]).filter(table => tables.some(row => row.CurrentTable === table));
    const operation = /^(SaveChanges)/.test(site.operation) ? 'SAVE_TRACKED_UNIT_OF_WORK_REVIEW' : /^(SqlQuery|FromSql|ExecuteReader|ExecuteScalar)/.test(site.operation) ? 'READ_OR_MUTATING_FUNCTION_REVIEW'
        : /^(ExecuteDelete)/.test(site.operation) ? 'DELETE' : /^(ExecuteUpdate)/.test(site.operation) ? 'UPDATE' : /^(BeginTransaction)/.test(site.operation) ? 'TRANSACTION' : 'SQL_EXECUTION_REQUIRES_STATEMENT_REVIEW';
    const row = { Path: site.path, Symbol: site.symbol, Line: site.line, Operation: operation, Tables: join(knownTables) || 'UNRESOLVED_SQL_OR_TRACKED_TARGET', Columns: 'SEE_STATIC_SQL_OR_TRACKED_ENTITIES', Evidence: `ROSLYN_SQL_SITE:${site.operation}:resolved=${site.resolved}`,
        DirectCallers: join([...(callers.get(site.symbol) ?? [])]), EntryPointCandidates: roots(site.symbol).join('; '), ActorProfileModuleMarkers: join(method?.scopeMarkers ?? []),
        TargetLogic: 'ENTITY_OWNER_FROM_TABLE_MATRIX', TargetService: 'AREA_AND_OPERATION_FROM_AUTHORIZED_ENTRY', Review: 'STATIC_BINDING_NOT_VERIFIED_RUNTIME_OR_AUTH_CHAIN' };
    accessRows.push(row);
    for (const table of knownTables) { if (!accessByTable.has(table)) accessByTable.set(table, []); accessByTable.get(table).push(row); }
}
const targetContract = read('docs/CLEAN_CUT_DATABASE.md');
const requiredTargets = new Set();
for (const line of targetContract.split('\n')) {
    if (line.startsWith('| `')) for (const match of line.split('|')[1].matchAll(/`([A-Z][A-Za-z0-9]+)`/g)) requiredTargets.add(match[1]);
}
for (const name of ['MediaAssetTypes', 'MediaTrackTypes']) requiredTargets.add(name);
for (const table of tables) {
    const relation = live.find(row => row.name === table.CurrentTable);
    const columns = catalog.columns.filter(row => row.schema === 'public' && row.table === table.CurrentTable);
    const constraints = catalogConstraints(table.CurrentTable);
    const mapping = dispositions.get(table.CurrentTable) ?? { action: 'KEEP', targets: [table.CurrentTable], reason: 'Retain current distinct responsibility as a candidate under CLEAN_CUT_DATABASE §4; no approved replacement/removal. KEEP means domain retained, all DDL designed fresh; requires field/consumer review.', fields: 'Actual catalog fields/relationships listed; ordinary PK/FK bigint, account/profile ownership and persisted enums/timestamps reviewed in B; no old DDL copied.', owner: domainFor(table.CurrentTable) };
    const accesses = accessByTable.get(table.CurrentTable) ?? [];
    Object.assign(table, { RowKind: table.CurrentTable === '__EFMigrationsHistory' ? 'FRAMEWORK' : 'IST_OR_HISTORICAL', LiveStatus: relation ? 'CONFIRMED_ACTIVE' : 'CONFIRMED_ABSENT',
        ColumnCount: relation ? columns.length : table.ColumnCount, ColumnsPgTypes: relation ? columns.map(column => `${column.column}:${column.type}${column.maximumLength ? '(' + column.maximumLength + ')' : ''};nullable=${column.nullable};default=${column.default ?? ''};identity=${column.identity}`).join(' | ') : table.ColumnsPgTypes,
        PrimaryKey: relation ? constraints.filter(row => row.type === 'p').map(row => row.definition).join('; ') : table.PrimaryKey,
        IndexedColumns: relation ? catalog.indexes.filter(row => row.table === table.CurrentTable).map(row => row.definition).join(' | ') : table.IndexedColumns,
        ForeignKeysFromSnapshot: relation ? constraints.filter(row => row.type === 'f').map(row => row.definition).join(' | ') : table.ForeignKeysFromSnapshot,
        ChecksAndUnique: constraints.filter(row => ['c','u'].includes(row.type)).map(row => `${row.name}:${row.definition}`).join(' | '),
        TargetTableAction: mapping.action, TargetTables: mapping.targets.join('; '), TargetDomainCandidate: mapping.owner, OwnerLogicCandidate: `Jularr.Logic.${mapping.owner}`,
        TargetReadServices: `Jularr.Service.<authorized User|Admin|System>.${mapping.owner}.V1`, DecisionReason: mapping.reason, FieldAndFkDisposition: mapping.fields,
        DirectAccessSymbols: join(accesses.map(row => row.Symbol)), DirectAccessPaths: join(accesses.map(row => row.Path)),
        ConsumerEntryCandidates: join(accesses.flatMap(row => row.EntryPointCandidates.split('; '))),
        DispositionReview: dispositions.has(table.CurrentTable) ? 'EXPLICIT_CONTRACT_AND_DOMAIN_SOURCE_DISPOSITION' : requiredTargets.has(table.CurrentTable) ? 'EXPLICIT_TARGET_CONTRACT_FRESH_RESPONSIBILITY_FIELD_REVIEW' : 'PROPOSED_DISTINCT_RESPONSIBILITY_REQUIRES_REVIEW',
        Resolution: relation && !accesses.length && table.CurrentTable !== '__EFMigrationsHistory' ? 'ACTIVE_TABLE_NO_BOUND_ACCESS_REVIEW_REQUIRED' : 'DOMAIN_DISPOSITION_RECORDED_CALLCHAIN_AND_FIELDS_NOT_FULLY_SIGNED',
        SourceSha: sha, Evidence: join([table.Evidence, 'docs/_phase_a_raw_schema_0.json']) });
}
for (const target of requiredTargets) {
    if (!tables.some(row => row.CurrentTable === target)) tables.push({ CurrentTable: target, RowKind: 'TARGET_ONLY', LiveStatus: 'NOT_PRESENT_TARGET_NEW', TargetTableAction: 'NEW', TargetTables: target,
        TargetDomainCandidate: domainFor(target), OwnerLogicCandidate: `Jularr.Logic.${domainFor(target)}`, TargetReadServices: `Jularr.Service.<authorized User|Admin|System>.${domainFor(target)}.V1`,
        DecisionReason: 'Explicit target table in CLEAN_CUT_DATABASE §3/FK contract; fresh create, no old-data migration.',
        FieldAndFkDisposition: 'Target contract authoritative; exact DDL in B, no invented columns.',
        DirectAccessSymbols: 'TARGET_NOT_IMPLEMENTED', Resolution: ['AccountPermissions','ImageGenerationPresets'].includes(target) ? 'CONDITIONAL_TARGET_REQUIRES_PRODUCT_DECISION' : 'EXPLICIT_TARGET_DOMAIN_OWNER_RECORDED', SourceSha: sha, Evidence: 'docs/CLEAN_CUT_DATABASE.md §3' });
}
for (const table of tables) table.TargetSources = join(tables.filter(source => (source.TargetTables ?? '').split('; ').includes(table.CurrentTable) && source.RowKind !== 'TARGET_ONLY').map(source => source.CurrentTable));
tables.sort((left,right) => left.CurrentTable.localeCompare(right.CurrentTable, 'en'));
csvWrite(tableFile, tables);
csvWrite('docs/DATABASE_CUTOVER_SEMANTIC_ACCESS.csv', accessRows.sort((left,right) => left.Path.localeCompare(right.Path) || left.Line-right.Line));
const tracked = execFileSync('git', ['ls-tree','-r','--name-only','origin/dev'], { encoding:'utf8' }).trim().split('\n');
const bodyPaths = tracked.filter(name => /^(src|clients|tests)\//.test(name) && /\.(cs|cshtml|js|ts|kt|kts)$/.test(name) && !/\/Migrations\//.test(name));
const bootstrapPaths = tracked.filter(name => /(^|\/)(Dockerfile|compose[^/]*\.ya?ml|appsettings[^/]*\.json|[^/]*\.csproj)$/.test(name)
    || name.startsWith('.agent/') && /\.ya?ml$/.test(name) || name.startsWith('.github/workflows/') || name.startsWith('scripts/') && /\.(mjs|sh|ps1)$/.test(name));
const sourcePaths = unique([...bodyPaths, ...bootstrapPaths]);
const consumers = sourcePaths.map(name => {
    const text = read(name), fileMethods = audit.methods.filter(method => method.path === name), accesses = accessRows.filter(row => row.Path === name);
    const transport = /ClientApiRoutes|PlaybackSessionClient|fetch\(|Map(Get|Post|Put|Delete|Patch)|MapGroup|@inject|OnPost|WorkId|workId|ProfileId|profileId|BackgroundService|IHostedService|GetDbConnection|SqlQuery|SaveChanges|Npgsql|DbContext|IndexedDB|indexedDB|SQLite|Sqlite/.test(text);
    const kind = bootstrapPaths.includes(name) ? 'BOOTSTRAP_BUILD_OR_CONFIG' : name.startsWith('tests/') ? 'TEST' : name.startsWith('clients/') ? 'ANDROID_MOBILE_TV_OR_SHARED' : name.endsWith('.cshtml') ? 'RAZOR_VIEW' : /\.(js|ts)$/.test(name) ? 'BROWSER_CLIENT' : /BackgroundService|IHostedService/.test(text) ? 'WORKER_OR_HOSTED' : /Endpoints\.cs$/.test(name) ? 'API' : 'CSHARP_SOURCE';
    const methodSymbols = new Set(fileMethods.map(method=>method.symbol));
    const indirect = audit.edges.filter(edge=>methodSymbols.has(edge.caller)).map(edge=>edge.callee);
    return { SourcePath:name, Domain:name.split('/Features/')[1]?.split('/')[0] ?? kind, SourceKind:kind,
        SourceReview:accesses.length ? 'BODY_AND_ROSLYN_DIRECT_ACCESS_INDEXED_CHAIN_REVIEW_REQUIRED' : transport ? 'BODY_SCANNED_CONTRACT_OR_ID_USAGE_REVIEW_REQUIRED' : 'BODY_SCANNED_NO_DIRECT_DB_OR_TRANSPORT_PATTERN',
        CandidateTargetLogicOrService:join(accesses.map(row => row.TargetLogic)), IndexedReadWriteReferences:join(accesses.map(row => `${row.Tables}:${row.Operation}:${row.Line}`)),
        ReferencedSourceSymbols:join(indirect), IdentityMarkers:join([...text.matchAll(/\b(AccountId|accountId|ProfileId|profileId|WorkId|workId|EpisodeId|episodeId|GameId|gameId|SourceId|sourceId)\b/g)].map(match => match[0])),
        Notes:'Static source/semantic index, not verified runtime dispatch, permissions or native DTO compatibility.', SourceSha:sha };
});
csvWrite('docs/DATABASE_CUTOVER_CONSUMERS.csv', consumers);
for (const [file, select] of [['RAZOR_DB_REFERENCES', row => row.Path.includes('/Pages/')], ['FEATURE_DB_REFERENCES', row => !row.Path.includes('/Pages/')]]) {
    const paths = unique([...accessRows.filter(select).map(row => row.Path), ...sourcePaths.filter(name => name.startsWith('src/') && select({Path:name}) && /\bAppDbContext\b/.test(read(name)))]);
    csvWrite(`docs/DATABASE_CUTOVER_${file}.csv`, paths.map(name => ({ SourcePath:name, Evidence:'LOCAL_BODY_DB_CONTEXT_REFERENCE_OR_ROSLYN_ACCESS', DirectAccessCount:accessRows.filter(row => row.Path === name).length, Symbols:join(accessRows.filter(row => row.Path === name).map(row => row.Symbol)), Review:'REQUIRES_AUTHORIZED_CONSUMER_CHAIN_REVIEW', SourceSha:sha })));
}
csvWrite('docs/DATABASE_CUTOVER_ACCESS_REFERENCES.csv', unique(accessRows.map(row => row.Path)).map(name => ({SourcePath:name, DirectAccessCount:accessRows.filter(row => row.Path === name).length, Evidence:'LOCAL_ROSLYN_BINDING_AND_SQL_SITE', Review:'STATIC_NOT_RUNTIME_PROOF', SourceSha:sha})));
const routePath = 'clients/android/core-api/src/main/kotlin/de/juloc/jularr/core/api/ClientApiRoutes.kt';
const clientPath = 'clients/android/core-api/src/main/kotlin/de/juloc/jularr/core/api/HttpJularrClientApi.kt';
const apiPath = 'src/Jularr.Web/Features/ClientApi/ClientApiEndpoints.cs';
const nativeMappings = {
    ApiVersion:['METADATA','Accounts','PROTOCOL_VERSION_2'], Base:['METADATA','Accounts','PREFIX_ONLY'],
    Capabilities:['GET','Instance','ANONYMOUS_CAPABILITY_READ'], Login:['POST','Accounts','ANONYMOUS_LOGIN_RATE_LIMIT'],
    Logout:['POST','Accounts','ACCOUNT_COOKIE'], Me:['GET','Accounts','ACCOUNT_CURRENT_PROFILE'],
    Library:['GET','Library','CURRENT_PROFILE'], ContinueWatching:['GET','Progress','CURRENT_PROFILE'],
    PlaybackHistory:['GET','Progress','CURRENT_PROFILE'], Watchlist:['GET','Watchlist','PROFILE_PAGED_VISIBLE_MEDIA'],
    watchlistPage:['GET','Watchlist','PROFILE_PAGED_VISIBLE_MEDIA'], anime:['GET','Works','PROFILE_MEDIA_ACCESS_FILTER'],
    episode:['GET','Works','PROFILE_MEDIA_ACCESS_FILTER'], progress:['GET; PUT','Progress','PROFILE_MEDIA_ACCESS_FILTER'],
    player:['GET','Playback','PROFILE_MEDIA_ACCESS_FILTER'], offlineDownload:['GET','Offline','AUTHENTICATED_OFFLINE_GRANT'],
    OfflineProgress:['POST','Progress','AUTHENTICATED_PROFILE_SYNC'], offlineLibraryManifest:['GET','Offline','AUTHENTICATED_OFFLINE_GRANT'],
    offlineLibraryChapter:['GET','Offline','AUTHENTICATED_OFFLINE_GRANT'], offlineLibraryAsset:['GET','Offline','AUTHENTICATED_OFFLINE_GRANT'],
    OfflineLibrarySync:['POST','Offline','AUTHENTICATED_PROFILE_SYNC'], cues:['GET','Subtitles','PROFILE_MEDIA_ACCESS_FILTER'],
    media:['GET','Playback','AUTHENTICATED_STREAM_RESOURCE'], mediaAvailability:['GET','Library','PROFILE_MEDIA_ACCESS_FILTER_FRESH_PROBE'],
    fallback:['GET','Playback','AUTHENTICATED_STREAM_RESOURCE'], hls:['GET','Playback','AUTHENTICATED_STREAM_RESOURCE'],
    term:['GET; PUT /state','Learning','CURRENT_PROFILE'], rootAvailability:['GET','Library','OWNER'], testRoot:['POST','Library','OWNER'],
    wakeRoot:['POST','Library','OWNER_RATE_LIMIT'], TtsPreferences:['GET; PUT','Profiles','CURRENT_PROFILE'], SpeechModels:['GET','Speech','AUTHENTICATED'],
    PairingStart:['POST','Accounts','ANONYMOUS_RATE_LIMIT'], PairingPoll:['POST','Accounts','ANONYMOUS_DEVICE_CODE_RATE_LIMIT']
};
const routeLines = read(routePath).split('\n');
const nativeRows = [];
for (let index=0;index<routeLines.length;index++) {
    const match = routeLines[index].match(/^\s*(?:const val|fun) (\w+)/); if (!match) continue;
    const symbol = match[1], mapping = nativeMappings[symbol];
    assert.ok(mapping, `Unmapped native route ${symbol}`);
    const [verbs,domain,scope] = mapping;
    const endpoint = symbol.startsWith('Pairing') ? 'src/Jularr.Web/Features/Pairing/DevicePairingEndpoints.cs'
        : symbol.startsWith('offlineLibrary') || symbol === 'OfflineLibrarySync' ? 'src/Jularr.Web/Features/ClientApi/ClientApiOfflineLibraryEndpoints.cs'
        : ['offlineDownload','OfflineProgress'].includes(symbol) ? 'src/Jularr.Web/Features/ClientApi/ClientApiOfflineEndpoints.cs' : apiPath;
    const nextDeclaration = routeLines.findIndex((line,lineIndex)=>lineIndex>index && /^\s*(?:const val|fun) /.test(line));
    const declarationBody = routeLines.slice(index,nextDeclaration<0?routeLines.length:nextDeclaration).join('\n');
    const routeExpressions = [...declarationBody.matchAll(/"([^"\n]*\$(?:Base|Watchlist)[^"\n]*)"/g)].map(match=>match[1]);
    const usedBy = bodyPaths.filter(name=>name.startsWith('clients/') && name.endsWith('.kt') && read(name).includes(`ClientApiRoutes.${symbol}`));
    nativeRows.push({NativeConsumer:routePath,RouteSymbol:symbol,CurrentPathExpression:routeExpressions.join(' ; ') || routeLines[index].trim(),SourceLine:index+1,
        CurrentVerbs:verbs,BackendSource:endpoint,NativeCallers:join(usedBy),CurrentScope:scope,
        TargetServiceOperation:`Jularr.Service.${scope.startsWith('OWNER')?'Admin':'User'}.${domain}.V1`,TargetLogic:`Jularr.Logic.${domain}`,
        TargetRoute:verbs==='METADATA'?'PROTOCOL_METADATA':'/api/v1/...; exact adapter URL defined with DTOs in E',
        MigrationGate:'ATOMIC_NATIVE_DTO_ID_SESSION_CACHE_CUTOVER_NO_BACKFILL',
        Review:'ROUTE_CONSTRUCTOR_AND_ENDPOINT_REGISTRATION_REVIEWED_AUTH_CHAIN_PARTIAL',
        Notes:['ApiVersion','Base'].includes(symbol)?'Protocol 2 and URL v1 are separate version contracts.'
            : ['hls','fallback','player','mediaAvailability','testRoot','wakeRoot'].includes(symbol)?'External IO/session creation must move through Logic Execute; pure Read must not start those effects.'
            : domain==='Offline' || symbol.includes('Sync') || symbol==='OfflineProgress'?'Invalidate old stored Work/Profile/unit IDs and authenticated grant generation at reset; filesystem assets not database rows.'
            : 'Shared core-api is used by Mobile/TV; preserve transport DTO/paging and resource/module policy in E.',SourceSha:sha});
}
const sessionPath = 'clients/android/core-session/src/main/kotlin/de/juloc/jularr/core/session/PlaybackSessionClient.kt';
for (const [symbol,verbs,route,scope] of [
    ['Create','POST','/playback-sessions/','AUTHENTICATED_PROFILE'], ['Get','GET','/playback-sessions/{id}','OWNER_PROFILE'],
    ['Update','PUT','/playback-sessions/{id}/state','OWNER_PROFILE_EXPECTED_REVISION'], ['Pairing','POST','/playback-sessions/{id}/pairing','OWNER_PROFILE'],
    ['Pair','POST','/playback-sessions/pair','ANONYMOUS_BOUND_SHORT_TTL_CODE'],
    ['ParticipantState','GET','/playback-sessions/{id}/participant-state','ANONYMOUS_BOUND_PARTICIPANT_TOKEN'],
    ['Commands','POST','/playback-sessions/{id}/commands','ANONYMOUS_BOUND_PARTICIPANT_TOKEN_COMMAND_DEDUP'],
    ['Revoke','POST','/playback-sessions/{id}/revoke','OWNER_PROFILE'], ['End','DELETE','/playback-sessions/{id}','OWNER_PROFILE'],
    ['Hub','SIGNALR','returned hub URL: state/command/session-ended','OWNER_OR_BOUND_PARTICIPANT_TOKEN']
]) nativeRows.push({NativeConsumer:sessionPath,RouteSymbol:`PlaybackSessions.${symbol}`,CurrentPathExpression:`/api/client/v1${route}`,CurrentVerbs:verbs,
    BackendSource:'src/Jularr.Web/Features/PlaybackSessions/PlaybackSessionEndpoints.cs; PlaybackSessionHub.cs; PlaybackSessionStore.cs',CurrentScope:scope,
    TargetServiceOperation:'Jularr.Service.User.PlaybackSessions.V1 restricted participant caller policy',TargetLogic:'Jularr.Logic.PlaybackSessions',TargetRoute:'/api/v1/...; adapter/Hub contract in E',
    MigrationGate:'DROP_OLD_SESSION_AND_PARTICIPANT_HANDLES_REAUTHENTICATE_AT_ATOMIC_CUTOFF',Review:'ENDPOINT_FAMILY_AND_IN_MEMORY_STORE_REVIEWED_NATIVE_ACTIONS_PARTIAL',
    Notes:'Current dictionary-backed companion sessions are distinct from SQL ActiveSessions; preserve revision/dedup/pairing/expiry/revoke and avoid claiming current durable storage.',SourceSha:sha});
csvWrite('docs/DATABASE_CUTOVER_ANDROID_ROUTES.csv',nativeRows);
const migrations = csvRead('docs/DATABASE_CUTOVER_MIGRATIONS.csv');
for (const migration of migrations) {
    const id = migration.MigrationFile.replace(/\.cs$/, '');
    migration.LiveApplied = catalog.migrationHistory.some(row => row.id === id) ? 'YES' : 'NO';
    migration.LiveProductVersion = catalog.migrationHistory.find(row => row.id === id)?.productVersion ?? '';
    migration.TargetDisposition = 'OLD_CHAIN_EXCLUDED_NEW_SINGLE_BASELINE_NO_BACKFILL'; migration.SourceSha = sha;
}
csvWrite('docs/DATABASE_CUTOVER_MIGRATIONS.csv', migrations);
const liveEvidence = {capturedAt:catalogCapturedAt,reconciledAt:capturedAt, sourceSha:sha, referenceInstance:'User-confirmed local dev UI demo, loopback PostgreSQL 5433, database jularr_demo', verifiedReadOnly:true, productRowsExported:false, schema:catalog,
    reconciliation:{originalCandidates:156,activeApplicationTables:live.length-1,frameworkTables:1,absentCandidates:tables.filter(row=>row.LiveStatus==='CONFIRMED_ABSENT').map(row=>row.CurrentTable)},
    seedEvidence:'Existing migrationBuilder InsertData/raw INSERT bodies in migration evidence JSON. No product/secret rows exported; public PostgreSQL enums absent.'};
writeJson('docs/_phase_a_raw_schema_0.json', liveEvidence);
writeJson('docs/_phase_a_code_part_0.json', {capturedAt,sourceSha:sha,source:audit.source,sourceFiles:audit.sourceFiles,aspNetReferenceVersion:audit.aspNetReferenceVersion,skippedNativeReferences:audit.skippedNativeReferences,diagnosticExamples:audit.diagnosticExamples,invocationCount:audit.invocationCount,unresolvedInvocations:audit.unresolvedInvocations,diagnostics:audit.diagnostics,
    edges:audit.edges,methods:audit.methods.map(method=>({symbol:method.symbol,path:method.path,line:method.line,scopeMarkers:method.scopeMarkers,sqlTables:method.sqlTables,writeMarkers:method.writeMarkers,routes:method.routes})),
    limitation:'Declaration-scoped graph includes lambdas; unresolved invocations, DI/interface/delegate dispatch and authorization remain review, not passed gate.'});
for (let part=1;part<8;part++) {
    const start=Math.floor((part-1)*consumers.length/7),end=Math.floor(part*consumers.length/7);
    writeJson(`docs/_phase_a_code_part_${part}.json`,{capturedAt,sourceSha:sha,review:'LOCAL_BODY_SCAN_NOT_COMPLETE_RUNTIME_CALLGRAPH',files:consumers.slice(start,end)});
}
const decisions={
945:['INTEGRATE_BEFORE_FREEZE','Canonical inventory; draft while gate has gaps. No schema/runtime migration.'],
941:['INTEGRATE_BEFORE_FREEZE','Binding architecture selected by user/#880; reconcile documentation precedence before freeze.'],
943:['INTEGRATE_BEFORE_FREEZE','Schema-independent SQL/Service foundation; CI/runtime review required before integration.'],
944:['INTEGRATE_BEFORE_FREEZE','Shared presentation only; bind target service metadata/DTOs in E.'],
937:['PORT_TO_TARGET','Jellyfin adapter survives; map canonical Work/Profile grants in E.'],
920:['PORT_TO_TARGET','Plex grants/reconciliation/login-link Purpose and page/worker paths must survive fresh Account/Profile/Work; no old migration copy.'],
929:['PORT_TO_TARGET','Current-dev integrated WAN/HLS/rendition candidate; Playback owner choice still unconfirmed.'],
928:['DEFER_ALTERNATIVE','Alternative integration overlaps #929; owner selects integration, port only nonduplicated required behavior.'],
923:['DEFER_ALTERNATIVE','Rendition-only overlap with #929; preserve policy/tests through chosen integration.'],
921:['DEFER_ALTERNATIVE','WAN-only overlap with #929; preserve required policy/tests through chosen integration.'],
919:['DEFER_ALTERNATIVE','HLS-only overlap with #929; preserve pacing/tests through chosen integration.'],
918:['PORT_TO_TARGET','Opt-in preparation/ROI independent capability; review asset identity/policy in E.'],
917:['PORT_TO_TARGET','Native/TV routes/DTOs/sessions/offline IDs move with backend; current routes stay until E.'],
842:['PORT_TO_TARGET','Notification channel/preference policy -> target Profile/type lookup; no old importer/migration.'],
768:['PORT_TO_TARGET','Request-policy UI -> canonical authorized Acquisition contract.'],
761:['DEFER_FEATURE_EXTENSION','Unmerged account groups extend product model; target built-in roles remain. Requires explicit group contract, no existing dev capability removal.'],
608:['DEFER_PRESENTATION_ONLY','Theme colocation has no schema contribution; reconcile shared UI owner after freeze.']
};
const delta=prs.map(({pr,files})=>{
    const decision=decisions[pr.number]??['REQUIRES_REVIEW','New PR after handoff'];
    const paths=files.map(file=>file.filename),ddl=files.filter(file=>/\/Migrations\/.*\.cs$/.test(file.filename));
    const createNames=unique(ddl.flatMap(file=>[...(file.patch??'').matchAll(/CreateTable\([\s\S]*?name:\s*"([^"]+)"/g)].map(match=>match[1])));
    return {PR:pr.number,Title:pr.title,Head:pr.headRefName,HeadSha:pr.headRefOid,Base:pr.baseRefName,Draft:pr.isDraft,ChangedFiles:files.length,PageTruncated:false,
        MigrationOrModelPaths:join(paths.filter(name=>/\/Migrations\/.*\.cs$|\/AppDbContext\.cs$/.test(name))),NewTableCandidates:createNames.join('; '),
        DatabaseSqlPaths:join(paths.filter(name=>/\/(Data|Infrastructure\/Sql|Service|Logic)\/|Store\.cs$/.test(name))),
        ApiOrClientPaths:join(paths.filter(name=>/^clients\/|\/Pages\/|Endpoints\.cs$/.test(name))),Decision:decision[0],Reason:decision[1],
        DecisionAuthority:'PHASE_A_RECOMMENDATION_NO_MERGE_AUTHORIZATION',Owner:pr.author?.login??'UNKNOWN',OwnerAgreement:'NO_NEW_OWNER_ACK_RECORDED',
        UpdatedAt:pr.updatedAt,FilePaths:join(paths),Evidence:'GitHub REST paginated files incl patches; fixed head SHA; ledger read'};
});
csvWrite('docs/DATABASE_CUTOVER_PR_DELTA.csv',delta);
const reviewedChains = [
    {id:'ADMIN_STORAGE_ROOT',entry:'src/Jularr.Web/Pages/Admin/System.cshtml.cs:114',chain:'OnPostAddAsync -> db.LibraryRoots.Add/SaveChanges -> root content assignments/routing; edit/delete/wake handlers share admin boundary',scope:'[Authorize Policy=JularrPolicies.AdminSystem], root/name/path/type validation; current error rollback retained',targets:'Admin.Library.V1 -> Logic.Library',evidence:'Verified direct Razor mutation, never pure SELECT; provider/filesystem wake is Execute.'},
    {id:'PLEX_LOGIN_LINK',entry:'src/Jularr.Web/Pages/Account/Plex.cshtml.cs:87',chain:'OnPostStartAsync -> provider PIN -> expired challenge delete/PlexLoginAttempts add/save -> poll/finish auth',scope:'Setup/owner presence; HTTPS or loopback, nonce hash and five-minute expiry; link requires current account/local password policy',targets:'User.Accounts.V1 restricted auth policy -> Logic.Accounts',evidence:'#920 Purpose delta: login/link preserved; AccountExternalLogins distinct from profile service connections; external I/O not inside SQL transaction.'},
    {id:'PROFILE_PROGRESS',entry:'src/Jularr.Web/Features/ClientApi/ClientApiEndpoints.cs:153',chain:'GET/PUT progress -> EpisodeProgressService -> VideoProgressService -> MediaProgress/MediaPlaybackHistory',scope:'Authenticated group, CurrentAccountContext.ProfileId; ClientVideoAccessFilter/appShell visibility; target existence checked',targets:'User.Progress.V1 -> Logic.Progress',evidence:'VideoProgress.cs UpdateAsync:165 validates target and explicit completion; seek alone does not set completed. Legacy GUID resolution removed only with consumer switch.'},
    {id:'WATCHLIST_PAGING',entry:'src/Jularr.Web/Features/ClientApi/ClientApiEndpoints.cs:234',chain:'GET watchlist -> PageRequest validation -> AppShell.GetMediaAccessAsync -> WatchlistStore.GetEffectivePageAsync -> WatchlistLibraryResolver',scope:'Current profile, visible media types, bounded page/pageSize; follow/ignore and franchise policy',targets:'User.Watchlist.V1; mutation Logic.Watchlist',evidence:'HttpJularrClientApi.kt:127 consumes totalCount/hasMore. Target read projects canonical Works/Profile; list query must not initiate NAS/provider work.'},
    {id:'ACQUISITION_DYNAMIC_DI',entry:'src/Jularr.Web/Features/Acquisition/Wanted/WantedAcquisitionService.cs:137',chain:'ExecuteAsync -> ProcessOnceAsync -> IWantedSource + IWantedRequestHandler dispatch -> AccessStore/OperationStore -> provider/download/import',scope:'IInstanceModuleService Acquisition/media capability upper bound; source/job cancellation/time budgets',targets:'System.Acquisition.V1 -> Logic.Acquisition and Logic.Operations',evidence:'Program.cs:610-656 registers Anime/Movie/TV/Manga/LightNovel/Book/Music/Audiobook handlers; 631/635/686/694/780/784 sources. Source graph alone omits interface dispatch.'},
    {id:'BOOTSTRAP_OLD_CHAIN',entry:'src/Jularr.Web/Program.cs:965',chain:'Startup scope -> DatabaseMigrationBridge.UpgradeAsync -> EF MigrateAsync -> WorkIdMigrationFollowUp.RunAsync',scope:'Startup internal caller, epoch 4/minimum 4; runs in existing installation, not executed by this audit',targets:'System.DatabaseInstallation.V1 -> installation Logic; one fresh baseline',evidence:'Old history and mapping are only inventory. Current bridge files remain until atomic cutoff, no old rows/history backfilled.'},
    {id:'WORK_CHILD_GENERIC',entry:'src/Jularr.Web/Features/MediaCore/WorkService.cs:491',chain:'MoveStructureAsync -> RepointOrDropAsync<T> -> db.Set<T> -> per-Work unique key collision removal/repoint',scope:'sourceWorkId/targetWorkId; upstream merge authorization still to review',targets:'Logic.Works',evidence:'Six concrete T: WorkSeason/Episode/Volume/Chapter/Edition/Version. WorkId reflection and EF.Property are actual dynamic column use, not an unknown seventh table.'},
    {id:'TV_COMPANION_SESSIONS',entry:'src/Jularr.Web/Features/PlaybackSessions/PlaybackSessionEndpoints.cs:17',chain:'Native PlaybackSessionClient -> HTTP/Hub -> PlaybackSessionStore dictionary -> state/command broadcasts',scope:'OwnerProfileId/revision; restricted short-lived pairing and participant-token access; revoke/end',targets:'User.PlaybackSessions.V1 -> Logic.PlaybackSessions',evidence:'Store.cs:16 is in-memory. Distinct from SQL ActiveSessions. Native/Hub permissions and complete command paths still require final review.'},
    {id:'TV_DEVICE_PAIRING',entry:'src/Jularr.Web/Features/Pairing/DevicePairingEndpoints.cs:21',chain:'start -> DevicePairingStore; authenticated approve -> profile; poll -> OwnerAuth.GetEnabledAccountAsync -> cookie sign-in',scope:'Start/poll anonymous rate limits, approve authenticated rate limit, single-use expiring device code',targets:'User.Accounts.V1 restricted pairing caller policy -> Logic.Accounts',evidence:'Pairing store deliberately in-memory. New Account != Profile: approving profile cannot substitute AccountId in target login.'},
    {id:'MUSIC_SPECIALTIES',entry:'src/Jularr.Web/Features/Music/MusicModels.cs:26',chain:'MusicQuery/MusicLibraryService/MusicWantedSource -> MusicArtists/MusicAlbums -> Works/WorkTracks/MusicRecordings/WorkMonitoring',scope:'Music capability and actor/source attribution; all individual auth callers not signed off',targets:'User/Admin/System.Music.V1 by entry -> Logic.Music/Works/Acquisition',evidence:'Album WorkId is already PK/FK extension. Recording may appear on multiple albums; artist is not a competing Work root. Keep specialty responsibility, rebuild IDs/DDL fresh.'},
    {id:'SUBTITLE_POLICY',entry:'src/Jularr.Web/Features/Subtitles/SubtitleLanguageProfileModels.cs:11',chain:'SubtitleLanguageProfileService.ResolveAsync -> ordered items/cutoff, specific media/root -> global default -> subtitle acquisition/completeness',scope:'Owner-managed language policy; Item.ProfileId is subtitle policy FK, not personal ProfileId',targets:'Admin.Subtitles.V1 / System.Subtitles.V1 -> Logic.Subtitles',evidence:'Preserve forced/SDH/priority/cutoff and null-scope uniqueness. Technical streams do not replace cue content.'},
    {id:'NON_DATABASE_ID_STORES',entry:'src/Jularr.Web/Features/Tracking/AniListAccountStore.cs:88',chain:'LoadAsync -> MigrateLegacyOwnerAccountUnsafe -> protected filesystem connection; Save/Disconnect/sync mode remain filesystem effects',scope:'Validated profile key, protected tokens; no values exported',targets:'User.Connections.V1 -> Logic.Connections',evidence:'MigrateLegacyOwnerAccountUnsafe is not a PostgreSQL migration. Old profile filenames/provider assignments need approved rebinding or explicit reconnect in E; credential files are protected.'}
];
for (const row of accessRows) {
    const matching = reviewedChains.filter(chain=>chain.entry.split(':')[0] === row.Path);
    row.ManualChainEvidence = matching.map(chain=>chain.id).join('; ');
    row.ManualReviewScope = matching.length?'SELECTED_CHAIN_ONLY_NOT_ALL_METHODS_SIGNED':'NOT_MANUALLY_SIGNED';
}
csvWrite('docs/DATABASE_CUTOVER_SEMANTIC_ACCESS.csv',accessRows);
const summary={capturedAt,catalogCapturedAt,conditionalTargets:["AccountPermissions","ImageGenerationPresets"],sourceSha:sha,applicationTables:live.length-1,frameworkTables:1,columns:catalog.columns.filter(row=>row.table!=='__EFMigrationsHistory').length,allColumns:catalog.columns.length,
    constraints:catalog.constraints.length,indexes:catalog.indexes.length,publicFunctions:catalog.functions.length,extensions:catalog.extensions,views:catalog.views.length,triggers:catalog.triggers.length,
    migrations:migrations.length,liveMigrations:catalog.migrationHistory.length,sourceFiles:audit.sourceFiles,consumerSourcePaths:consumers.length,bodySourcePaths:bodyPaths.length,bootstrapPaths:bootstrapPaths.length,dbAccessSites:accessRows.length,dbAccessPaths:unique(accessRows.map(row=>row.Path)).length,
    dbSetSites:audit.accesses.length,sqlSites:audit.sqlSites?.length??0,declaredSymbols:audit.methods.length,sourceCallEdges:audit.edges.length,unresolvedInvocations:audit.unresolvedInvocations,
    unresolvedAccessTargets:accessRows.filter(row=>row.Tables.includes('UNRESOLVED')).length, proposedDispositions:tables.filter(row=>row.DispositionReview==='PROPOSED_DISTINCT_RESPONSIBILITY_REQUIRES_REVIEW').length,
    razorDbContextPaths:csvRead('docs/DATABASE_CUTOVER_RAZOR_DB_REFERENCES.csv').length,featureDbContextPaths:csvRead('docs/DATABASE_CUTOVER_FEATURE_DB_REFERENCES.csv').length,
    unmappedEntityTables:unique(accessRows.flatMap(row=>row.Tables==='UNRESOLVED_ENTITY_TABLE'?[row.Symbol]:[])),activeTablesWithoutStaticCandidate:tables.filter(row=>row.Resolution==='ACTIVE_TABLE_NO_BOUND_ACCESS_REVIEW_REQUIRED').map(row=>row.CurrentTable),
    tableRows:tables.length,newTargetRows:tables.filter(row=>row.RowKind==='TARGET_ONLY').length,requiredTargetNames:[...requiredTargets].sort(),manualReviewedChains:reviewedChains.length,nativeContractRows:nativeRows.length,openPrs:delta.length,gateA:'OPEN',
    passedCriteria:['LIVE_REFERENCE_CATALOG_READ_ONLY','MIGRATION_HISTORY_RECONCILED','ALL_SOURCE_BODIES_INDEXED','CURRENT_OPEN_PR_FILES_CAPTURED'],
    remainingCriteria:['FULL_AUTHORIZED_READER_WRITER_CONSUMER_CHAINS','FIELD_DISPOSITION_SIGNOFF','PR_OWNER_INTEGRATION_CHOICE_AND_FREEZE_ACK']};
writeJson('docs/_phase_a_search_sql_patterns_1.json',summary);
writeJson('docs/_phase_a_search_sql_patterns_2.json',{capturedAt,sourceSha:sha,review:'LOCAL_SOURCE_AUDIT',dbSetAccesses:audit.accesses.map(({chain,...row})=>row),sqlSites:audit.sqlSites??[]});
writeJson('docs/_phase_a_search_sql_patterns_3.json',{capturedAt,sourceSha:sha,openPrHeads:delta.map(row=>({pr:row.PR,head:row.HeadSha,decision:row.Decision,agreement:row.OwnerAgreement})),schemaDeltas:delta.filter(row=>row.MigrationOrModelPaths),workspaces,reviewedChains,
    startupRegistrations:bodyPaths.filter(name=>name.startsWith('src/') && name.endsWith('.cs')).flatMap(name=>read(name).split('\n').flatMap((line,index)=>/AddHostedService|AddScoped<.*IWanted(Source|RequestHandler)/.test(line)?[{path:name,line:index+1,registration:line.trim()}]:[])),
    gateA:'OPEN'});
assert.equal(new Set(tables.map(row=>row.CurrentTable)).size,tables.length,'Duplicate table rows');
assert.equal(new Set(consumers.map(row=>row.SourcePath)).size,consumers.length,'Duplicate source rows');
assert.equal(new Set(delta.map(row=>row.PR)).size,delta.length,'Duplicate PR rows');
assert.deepEqual(live.map(row=>row.name).sort(),tables.filter(row=>row.LiveStatus==='CONFIRMED_ACTIVE').map(row=>row.CurrentTable).sort(),'Missing live tables');
assert.equal(requiredTargets.size,[...requiredTargets].filter(target=>tables.some(row=>row.CurrentTable===target)).length,'Missing explicit target');
assert.ok(tables.every(row=>['KEEP','RENAME','MERGE','SPLIT','DROP','NEW'].includes(row.TargetTableAction)),'Invalid disposition');
assert.equal(migrations.filter(row=>row.LiveApplied==='YES').length,catalog.migrationHistory.length,'Migration history differs');
console.log(JSON.stringify(summary,null,2));
