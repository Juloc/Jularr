import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { csvRead, json, read } from './inventory-io.mjs';

export function verifyInventory(root = process.cwd()) {
    const document = name => path.join(root, 'docs', name);
    const summary = json(document('_phase_a_search_sql_patterns_1.json'));
    const evidence = json(document('_phase_a_raw_schema_0.json'));
    const tables = csvRead(document('DATABASE_CUTOVER_INVENTORY_TABLES.csv'));
    const migrations = csvRead(document('DATABASE_CUTOVER_MIGRATIONS.csv'));
    const consumers = csvRead(document('DATABASE_CUTOVER_CONSUMERS.csv'));
    const accesses = csvRead(document('DATABASE_CUTOVER_SEMANTIC_ACCESS.csv'));
    const prs = csvRead(document('DATABASE_CUTOVER_PR_DELTA.csv'));
    const native = csvRead(document('DATABASE_CUTOVER_ANDROID_ROUTES.csv'));
    for (const [rows, key] of [[tables,'CurrentTable'],[migrations,'MigrationFile'],[consumers,'SourcePath'],[prs,'PR']]) {
        assert.equal(new Set(rows.map(row=>row[key])).size, rows.length, `Duplicate ${key}`);
        assert.ok(rows.every(row=>row[key]), `Empty ${key}`);
    }
    assert.equal(evidence.schema.readOnly, 'on', 'Catalog was not read-only');
    assert.equal(evidence.verifiedReadOnly, true);
    assert.equal(evidence.productRowsExported, false);
    const live = evidence.schema.relations.filter(row=>['r','p'].includes(row.kind));
    assert.deepEqual(tables.filter(row=>row.LiveStatus==='CONFIRMED_ACTIVE').map(row=>row.CurrentTable).sort(),live.map(row=>row.name).sort(), 'Live table coverage differs');
    assert.equal(summary.applicationTables, live.filter(row=>row.name!=='__EFMigrationsHistory').length);
    assert.equal(summary.columns, evidence.schema.columns.filter(row=>row.table!=='__EFMigrationsHistory').length);
    assert.equal(summary.allColumns, evidence.schema.columns.length);
    assert.equal(summary.constraints, evidence.schema.constraints.length);
    assert.equal(summary.indexes, evidence.schema.indexes.length);
    assert.equal(summary.consumerSourcePaths, consumers.length);
    assert.equal(summary.tableRows, tables.length);
    assert.equal(summary.dbAccessSites, accesses.length);
    assert.equal(summary.dbAccessPaths, new Set(accesses.map(row=>row.Path)).size);
    assert.equal(summary.openPrs, prs.length);
    assert.equal(summary.nativeContractRows, native.length);
    assert.deepEqual(migrations.filter(row=>row.LiveApplied==='YES').map(row=>row.MigrationFile.replace(/\.cs$/,'')).sort(),evidence.schema.migrationHistory.map(row=>row.id).sort());
    for (const rows of [tables,migrations,consumers,prs,native]) {
        assert.ok(rows.every(row=>!row.SourceSha || row.SourceSha===summary.sourceSha), 'Mixed source SHAs');
    }
    assert.ok(tables.every(row=>['KEEP','RENAME','MERGE','SPLIT','DROP','NEW'].includes(row.TargetTableAction)), 'Invalid table action');
    assert.ok(tables.every(row=>row.OwnerLogicCandidate && !row.OwnerLogicCandidate.includes('REQUIRES_DOMAIN_REVIEW')), 'Missing domain owner');
    assert.ok(summary.requiredTargetNames.every(target=>tables.some(row=>row.CurrentTable===target)), 'Missing explicit target table');
    assert.ok(native.every(row=>row.TargetServiceOperation && row.TargetLogic), 'Missing native target owner');
    for (let part=1;part<8;part++) assert.equal(json(document(`_phase_a_code_part_${part}.json`)).sourceSha,summary.sourceSha);
    const context = json(document('_phase_a_search_sql_patterns_3.json'));
    assert.equal(context.sourceSha,summary.sourceSha);
    assert.ok(context.startupRegistrations.every(row=>row.path.startsWith('src/') && row.line>0 && !/[\r\n]/.test(row.registration) && row.registration.length<=280), 'Startup evidence contains a file body or invalid source line');
    const code = json(document('_phase_a_code_part_0.json'));
    assert.equal(code.sourceSha, summary.sourceSha);
    assert.equal(summary.sourceFiles, code.sourceFiles);
    assert.equal(summary.sourceCallEdges, code.edges.length);
    assert.equal(code.aspNetReferenceVersion, '10.0.12');
    for (const [index,line] of read(path.join(root,'scripts/cutover/Program.cs')).split('\n').entries()) {
        assert.ok(line.length<=280, `C# hard line limit: Program.cs:${index+1}`);
    }
    const gaps = {
        unresolvedSqlOrTrackedTargets: accesses.filter(row=>row.Tables.includes('UNRESOLVED')).length,
        unsignedAccessChains: accesses.filter(row=>row.Review!=='VERIFIED_AUTHORIZED_CHAIN').length,
        unsignedRelevantConsumers: consumers.filter(row=>/REVIEW_REQUIRED/.test(row.SourceReview)).length,
        proposedTableDispositions: tables.filter(row=>row.DispositionReview==='PROPOSED_DISTINCT_RESPONSIBILITY_REQUIRES_REVIEW').length,
        unsignedFields: tables.filter(row=>row.RowKind!=='TARGET_ONLY' && !row.Resolution.startsWith('VERIFIED_')).length,
        unconfirmedPrDecisions: prs.filter(row=>row.OwnerAgreement==='NO_NEW_OWNER_ACK_RECORDED').length
    };
    assert.equal(summary.unresolvedAccessTargets, gaps.unresolvedSqlOrTrackedTargets);
    assert.equal(summary.proposedDispositions, gaps.proposedTableDispositions);
    assert.equal(summary.gateA, Object.values(gaps).some(Boolean)?'OPEN':'PASSED', 'Gate A contradicts review gaps');
    return {sourceSha:summary.sourceSha,applicationTables:summary.applicationTables,consumerPaths:consumers.length,gaps,gateA:summary.gateA};
}
if (process.argv[1] && fileURLToPath(import.meta.url)===path.resolve(process.argv[1])) {
    const result = verifyInventory();
    console.log(JSON.stringify(result,null,2));
    if (process.argv.includes('--gate') && result.gateA!=='PASSED') process.exitCode=2;
}
