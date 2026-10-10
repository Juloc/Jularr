import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const docs = fileURLToPath(new URL('../docs/', import.meta.url));
const read = name => readFileSync(new URL(`../docs/${name}`, import.meta.url), 'utf8').replaceAll('\r\n', '\n');
const contracts = new Map();
for (const match of read('DATABASE_CUTOVER_PHASE_B_TYPE_CONTRACTS.cs').matchAll(/public enum (\w+) : byte\n\{\n([\s\S]*?)\n\}/g))
{
    const members = match[2].trim().split(',\n').map(line =>
    {
        const assignment = /^\s*(\w+) = (\d+)\s*$/.exec(line);
        if (!assignment || Number(assignment[2]) > 255)
        {
            throw new Error(`Invalid explicit byte member in ${match[1]}.`);
        }
        return {
            id: Number(assignment[2]),
            key: assignment[1].replace(/([a-z0-9])([A-Z])/g, '$1_$2').toLowerCase()
        };
    });
    if (contracts.has(match[1]) || new Set(members.map(member => member.id)).size !== members.length)
    {
        throw new Error(`Duplicate enum or byte value in ${match[1]}.`);
    }
    contracts.set(match[1], members);
}

const ddl = readdirSync(docs)
    .filter(name => /^DATABASE_CUTOVER_PHASE_B_\d{2}_.*_DRAFT\.sql$/.test(name))
    .sort()
    .map(read)
    .join('\n');
const verified = new Set();
let pending = 0;
let values = 0;
for (const line of read('DATABASE_CUTOVER_PHASE_B_ENUM_SEED_MANIFEST.csv').trim().split('\n').slice(1))
{
    const cells = [...line.matchAll(/(?:^|,)("(?:[^"]|"")*"|[^,]*)/g)]
        .map(match => match[1].startsWith('"') ? match[1].slice(1, -1).replaceAll('""', '"') : match[1]);
    if (cells.length !== 7)
    {
        throw new Error('Invalid enum-manifest row.');
    }
    if (cells[3] !== 'TARGET_CONTRACT_DEFINED')
    {
        pending++;
        continue;
    }

    const enumName = /: (\w+) : byte$/.exec(cells[5])?.[1];
    const members = contracts.get(enumName);
    if (!members || verified.has(enumName))
    {
        throw new Error(`Missing or reused byte contract for ${cells[0]}.`);
    }
    const manifest = members.map(member => `${member.id}:${member.key}`).join(';');
    if (cells[2] !== manifest)
    {
        throw new Error(`Manifest disagrees with ${enumName}.`);
    }

    const policyColumns = cells[0] === 'EventAudienceTypes' ? ',\\s*"RequiresProfile"'
        : cells[0] === 'NotificationEventCategoryTypes' ? ',\\s*"EventAudienceTypeId",\\s*"EventSeverityTypeId"' : '';
    const statements = [...ddl.matchAll(new RegExp(`INSERT INTO "${cells[0]}" \\("Id",\\s*"Key"${policyColumns}\\)\\s*VALUES\\s*([\\s\\S]*?);`, 'g'))];
    if (statements.length !== 1)
    {
        throw new Error(`Expected one static seed statement for ${cells[0]}.`);
    }
    const rowPattern = /\(\s*(\d+)\s*,\s*'([^']*)'((?:\s*,\s*(?:true|false|\d+))*)\s*\)/g;
    const actual = [...statements[0][1].matchAll(rowPattern)].map(match => ({
        id: Number(match[1]),
        key: match[2],
        policy: match[3].split(',').slice(1).map(value => value.trim())
    }));
    const expected = members.map(member => ({
        ...member,
        policy: cells[0] === 'EventAudienceTypes' ? [member.id === 1 ? 'true' : 'false']
            : cells[0] === 'NotificationEventCategoryTypes'
                ? [member.id === 8 ? '2' : '1', member.id === 8 ? '3' : [2, 4].includes(member.id) ? '2' : '1'] : []
    }));
    if (statements[0][1].replace(rowPattern, '').replace(/[\s,]/g, '') !== '' || JSON.stringify(actual) !== JSON.stringify(expected))
    {
        throw new Error(`DDL seeds disagree with ${enumName}.`);
    }
    verified.add(enumName);
    values += members.length;
}
const catalogTables = [...ddl.matchAll(/CREATE TABLE "(\w+Types)" \(/g)].map(match => match[1]);
if (pending !== 0 || verified.size !== catalogTables.length || verified.size !== contracts.size)
{
    throw new Error('Every persisted type catalog must have exactly one explicit byte contract, manifest row and DDL seed statement.');
}
process.stdout.write(`Verified ${verified.size} defined byte contracts and ${values} seed values; ${pending} catalogs still unresolved.\n`);
