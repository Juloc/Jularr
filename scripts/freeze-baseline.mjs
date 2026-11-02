// Not for ordinary work: node scripts/freeze-baseline.mjs
// Rewrites database/baseline.sha256, the hash of the frozen baseline (schema version 1). The baseline never changes:
// schema changes are new numbered migrations (database/migrations/NNNN_short_name.sql, docs/DATABASE_MIGRATIONS.md) and the guard test
// DatabaseBaselineTests.Baseline_MatchesTheFrozenHash fails on any other edit. The hash is the one DatabaseBaseline.Fingerprint(1) computes.
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const source = readFileSync(join(root, "src", "Jularr.Backend", "Service", "Core", "DatabaseBaseline.cs"), "utf8");
const list = /Baselines = \[([^\]]+)\]/.exec(source);
if (!list) throw new Error("The Baselines list was not found in DatabaseBaseline.cs.");

const names = [...list[1].matchAll(/"([A-Za-z]+)"/g)].map(match => match[1]);
let content = "";
for (const name of names) {
  content += name + "\n" + readFileSync(join(root, "database", name + ".sql"), "utf8").replaceAll("\r\n", "\n");
}

const hash = createHash("sha256").update(content, "utf8").digest("hex");
writeFileSync(join(root, "database", "baseline.sha256"), hash + "\n");
console.log("database/baseline.sha256 = " + hash);
