import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { verifyInventory } from './verify.mjs';
import { csvRead, csvWrite, json, writeJson } from './inventory-io.mjs';

function corruptEvidence(mutate, expected) {
    const prefix = path.join(os.tmpdir(),'jularr-phase-a-guard-');
    const directory = fs.mkdtempSync(prefix);
    try {
        fs.mkdirSync(path.join(directory,'docs'));
        fs.mkdirSync(path.join(directory,'scripts/cutover'),{recursive:true});
        for (const name of fs.readdirSync('docs').filter(name=>name.startsWith('DATABASE_CUTOVER_') && name.endsWith('.csv') || name.startsWith('_phase_a_') && name.endsWith('.json'))) {
            fs.copyFileSync(path.join('docs',name),path.join(directory,'docs',name));
        }
        fs.copyFileSync('scripts/cutover/Program.cs',path.join(directory,'scripts/cutover/Program.cs'));
        mutate(directory);
        assert.throws(()=>verifyInventory(directory),expected);
    } finally {
        const resolved = path.resolve(directory);
        assert.ok(resolved.startsWith(path.resolve(prefix)), 'Unsafe temporary cleanup target');
        fs.rmSync(resolved,{recursive:true});
    }
}
test('consistent captured evidence stays open while review gaps exist',()=> {
    const result = verifyInventory();
    assert.equal(result.gateA,'OPEN');
    assert.ok(result.gaps.unsignedAccessChains>0);
});
test('missing live table rejects inventory even when totals were changed',()=>corruptEvidence(root=> {
    const file = path.join(root,'docs/DATABASE_CUTOVER_INVENTORY_TABLES.csv');
    csvWrite(file,csvRead(file).filter(row=>row.CurrentTable!=='MediaProgress'));
},/Live table coverage/));
test('duplicate table decision rejects inventory',()=>corruptEvidence(root=> {
    const file = path.join(root,'docs/DATABASE_CUTOVER_INVENTORY_TABLES.csv');
    const rows = csvRead(file); rows.push(rows[0]); csvWrite(file,rows);
},/Duplicate CurrentTable/));
test('mixed consumer source commit rejects inventory',()=>corruptEvidence(root=> {
    const file = path.join(root,'docs/DATABASE_CUTOVER_CONSUMERS.csv');
    const rows = csvRead(file); rows[0].SourceSha='different-commit'; csvWrite(file,rows);
},/Mixed source SHAs/));
test('green gate cannot override unsigned authorization and disposition rows',()=>corruptEvidence(root=> {
    const file = path.join(root,'docs/_phase_a_search_sql_patterns_1.json');
    const summary = json(file); summary.gateA='PASSED'; writeJson(file,summary);
},/Gate A contradicts review gaps/));
test('startup metadata cannot contain copied source bodies',()=>corruptEvidence(root=> {
    const file = path.join(root,'docs/_phase_a_search_sql_patterns_3.json');
    const context = json(file); context.startupRegistrations[0].registration+='\nsecond source line'; writeJson(file,context);
},/Startup evidence/));
