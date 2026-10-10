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

for (const query of [
    {
        name: 'groups',
        file: 'DATABASE_CUTOVER_PHASE_B_ADMIN_QUERIES_DRAFT.sql',
        declaration: 'PREPARE phase_b_groups(bigint,integer,bigint) AS\n',
        parameters: ['ActorAccountId', 'PageSize', 'Offset']
    },
    {
        name: 'contexts',
        file: 'DATABASE_CUTOVER_PHASE_B_LEARNING_QUERIES_DRAFT.sql',
        declaration: 'PREPARE phase_b_contexts(bigint,bigint,uuid,integer,bigint) AS\n',
        parameters: ['ActorAccountId', 'ActiveProfileId', 'LearningUnitPublicId', 'PageSize', 'Offset']
    }
])
{
    let sql = readFileSync(fileURLToPath(new URL(`../docs/${query.file}`, import.meta.url)), 'utf8').replaceAll('\r\n', '\n').trim();
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
