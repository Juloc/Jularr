import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const outputDirectory = resolve(process.argv[2] ?? '/tmp');
const source = readFileSync(fileURLToPath(new URL('../docs/DATABASE_CUTOVER_PHASE_B_READ_QUERIES_DRAFT.sql', import.meta.url)), 'utf8').replaceAll('\r\n', '\n');
const boundaries = [
    source.indexOf('WITH "PageRoots" AS MATERIALIZED'),
    source.indexOf('-- 2. Bounded Continue list'),
    source.indexOf('-- 3. Logic-only short transaction'),
    source.indexOf('-- 4. Logic-only bounded optimistic checkpoint'),
    source.length
];

if (boundaries.some((position, index) => position < 0 || (index > 0 && position <= boundaries[index - 1])))
{
    throw new Error('Canonical SQL query sections are missing or out of order.');
}

const queries = [
    {
        name: 'watchlist',
        start: 'WITH "PageRoots" AS MATERIALIZED',
        declaration: 'PREPARE phase_b_watchlist(bigint,bigint,text,integer,bigint) AS\n',
        parameters: ['ActorAccountId', 'ActiveProfileId', 'UiLocale', 'PageSize', 'Offset']
    },
    {
        name: 'continue',
        start: 'SELECT',
        declaration: 'PREPARE phase_b_continue(bigint,bigint,integer,bigint) AS\n',
        parameters: ['ActorAccountId', 'ActiveProfileId', 'PageSize', 'Offset']
    },
    {
        name: 'claim',
        start: 'WITH claimed AS',
        declaration: 'PREPARE phase_b_claim(smallint,smallint,integer,text) AS\n',
        parameters: ['PendingStatusTypeId', 'RunningStatusTypeId', 'BatchSize', 'TrustedWorkerKey']
    },
    {
        name: 'progress_cas',
        start: 'UPDATE\n    "MediaProgress" AS progress',
        declaration: 'PREPARE phase_b_progress_cas(bigint,bigint,bigint,bigint) AS\n',
        parameters: ['MediaProgressId', 'ActorAccountId', 'ActiveProfileId', 'ExpectedRevision']
    }
];

mkdirSync(outputDirectory, { recursive: true });
for (const [index, query] of queries.entries())
{
    const section = source.slice(boundaries[index], boundaries[index + 1]);
    const start = section.indexOf(query.start);
    if (start < 0)
    {
        throw new Error(`Canonical ${query.name} query is missing.`);
    }

    let sql = section.slice(start).trim().replace(/;$/, '');
    for (const [parameterIndex, name] of query.parameters.entries())
    {
        const placeholder = `@${name}`;
        if (!sql.includes(placeholder))
        {
            throw new Error(`Canonical ${query.name} query is missing ${placeholder}.`);
        }
        sql = sql.replaceAll(placeholder, `$${parameterIndex + 1}`);
    }

    if (sql.includes('@'))
    {
        throw new Error(`Unbound SQL placeholder in canonical ${query.name} query.`);
    }

    writeFileSync(resolve(outputDirectory, `phase_b_${query.name}_prepared.sql`), query.declaration + sql + ';\n');
}

const authQueries = [
    ['auth_session', 'Service SESSION:', 'Service SESSIONS:', 'bytea,timestamptz', ['TokenHash', 'Now']],
    ['auth_sessions', 'Service SESSIONS:', 'Logic ROTATE:', 'bigint,integer,bigint', ['ActorAccountId', 'PageSize', 'Offset']],
    ['auth_rotate', 'Logic ROTATE:', 'Logic CHALLENGE:', 'bigint,bytea,bytea,bigint,timestamptz,timestamptz', ['ActorAccountId', 'OldTokenHash', 'NewTokenHash', 'ExpectedRotationRevision', 'Now', 'NewExpiresAt']],
    ['auth_challenge', 'Logic CHALLENGE:', 'Logic RECOVERY:', 'uuid,bytea,smallint,bigint,timestamptz,bigint,bigint', ['ChallengePublicId', 'BrowserTokenHash', 'PurposeTypeId', 'ActorAccountId', 'Now', 'ProviderId', 'ActiveProfileId']],
    ['auth_recovery', 'Logic RECOVERY:', 'Logic TOTP:', 'bigint,bytea,timestamptz', ['ActorAccountId', 'CodeHash', 'Now']],
    ['auth_totp', 'Logic TOTP:', 'Logic INVALIDATE:', 'bigint,bigint,bigint', ['ActorAccountId', 'FactorId', 'VerifiedStep']],
    ['auth_invalidate', 'Logic INVALIDATE:', null, 'bigint,bigint,timestamptz', ['ActorAccountId', 'ExpectedCredentialRevision', 'Now']]
].map(([name, start, end, types, parameters]) => ({
    name,
    file: 'DATABASE_CUTOVER_PHASE_B_AUTH_QUERIES_DRAFT.sql',
    start: `-- ${start}`,
    end: end ? `-- ${end}` : null,
    declaration: `PREPARE phase_b_${name}(${types}) AS\n`,
    parameters
}));

for (const query of [
    ...authQueries,
    {
        name: 'groups',
        file: 'DATABASE_CUTOVER_PHASE_B_ADMIN_QUERIES_DRAFT.sql',
        declaration: 'PREPARE phase_b_groups(bigint,integer,bigint) AS\n',
        parameters: ['ActorAccountId', 'PageSize', 'Offset']
    },
    {
        name: 'inbox',
        file: 'DATABASE_CUTOVER_PHASE_B_NOTIFICATION_QUERIES_DRAFT.sql',
        start: '-- Service READ:',
        end: '-- Logic ENSURE:',
        declaration: 'PREPARE phase_b_inbox(bigint,bigint,boolean,integer,bigint) AS\n',
        parameters: ['ActorAccountId', 'ActiveProfileId', 'UnreadOnly', 'PageSize', 'Offset']
    },
    {
        name: 'inbox_ensure',
        file: 'DATABASE_CUTOVER_PHASE_B_NOTIFICATION_QUERIES_DRAFT.sql',
        start: '-- Logic ENSURE:',
        end: '-- Logic RECORD:',
        declaration: 'PREPARE phase_b_inbox_ensure(uuid,bigint) AS\n',
        parameters: ['EventId', 'RecipientProfileId']
    },
    {
        name: 'inbox_record',
        file: 'DATABASE_CUTOVER_PHASE_B_NOTIFICATION_QUERIES_DRAFT.sql',
        start: '-- Logic RECORD:',
        declaration: 'PREPARE phase_b_inbox_record(bigint,bigint,uuid) AS\n',
        parameters: ['NotificationId', 'RecipientProfileId', 'EventId']
    },
    {
        name: 'notification_schedule',
        file: 'DATABASE_CUTOVER_PHASE_B_NOTIFICATION_SCHEDULING_QUERY_DRAFT.sql',
        declaration: 'PREPARE phase_b_notification_schedule(bigint,timestamptz) AS\n',
        parameters: ['NotificationDeliveryId', 'Now']
    },
    {
        name: 'notification_route',
        file: 'DATABASE_CUTOVER_PHASE_B_NOTIFICATION_ROUTE_QUERY_DRAFT.sql',
        declaration: 'PREPARE phase_b_notification_route(bigint,timestamptz,smallint[],text[]) AS\n',
        parameters: ['NotificationDeliveryId', 'Now', 'AvailableChannels', 'AvailablePushTransports']
    }
])
{
    let sql = readFileSync(fileURLToPath(new URL(`../docs/${query.file}`, import.meta.url)), 'utf8').replaceAll('\r\n', '\n').trim();
    if (query.start)
    {
        const start = sql.indexOf(query.start);
        const end = query.end ? sql.indexOf(query.end) : sql.length;
        if (start < 0 || end <= start)
        {
            throw new Error(`Canonical ${query.name} section is missing or out of order.`);
        }
        sql = sql.slice(start, end).trim();
    }
    for (const [index, parameter] of query.parameters.entries())
    {
        const placeholder = `@${parameter}`;
        if (!sql.includes(placeholder))
        {
            throw new Error(`Canonical ${query.name} query is missing ${placeholder}.`);
        }
        sql = sql.replaceAll(placeholder, `$${index + 1}`);
    }
    if (sql.includes('@'))
    {
        throw new Error(`Unbound SQL placeholder in canonical ${query.name} query.`);
    }
    writeFileSync(resolve(outputDirectory, `phase_b_${query.name}_prepared.sql`), query.declaration + sql + '\n');
}


const readerFile = readFileSync(fileURLToPath(new URL('../docs/DATABASE_CUTOVER_PHASE_B_READER_QUERIES_DRAFT.sql', import.meta.url)), 'utf8').replaceAll('\r\n', '\n');
const readerQueries = [
    {
        name: 'reader_bookmarks',
        start: '-- Service BOOKMARKS:',
        end: '-- Service HIGHLIGHTS:'
    },
    {
        name: 'reader_highlights',
        start: '-- Service HIGHLIGHTS:',
        end: null
    }
];

for (const query of readerQueries)
{
    const start = readerFile.indexOf(query.start);
    const end = query.end ? readerFile.indexOf(query.end) : readerFile.length;
    if (start < 0 || end <= start)
    {
        throw new Error(`Canonical ${query.name} section is missing or out of order.`);
    }

    let sql = readerFile.slice(start, end).trim();
    for (const [index, name] of ['ActorAccountId', 'ActiveProfileId', 'WorkPublicId', 'PageSize', 'Offset'].entries())
    {
        if (!sql.includes(`@${name}`))
        {
            throw new Error(`Canonical ${query.name} query is missing @${name}.`);
        }
        sql = sql.replaceAll(`@${name}`, `$${index + 1}`);
    }
    if (sql.includes('@'))
    {
        throw new Error(`Unbound SQL placeholder in canonical ${query.name} query.`);
    }
    writeFileSync(resolve(outputDirectory, `phase_b_${query.name}_prepared.sql`),
        'PREPARE phase_b_' + query.name + '(bigint,bigint,uuid,integer,bigint) AS\n' + sql + '\n');
}
