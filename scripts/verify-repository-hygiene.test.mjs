import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const script = join(dirname(fileURLToPath(import.meta.url)), "verify-repository-hygiene.mjs");
function check(files) {
  const dir = mkdtempSync(join(tmpdir(), "jularr-hygiene-"));
  try {
    execFileSync("git", ["init", "-q"], { cwd: dir });
    for (const [filename, data] of Object.entries(files)) {
      mkdirSync(dirname(join(dir, filename)), { recursive: true });
      writeFileSync(join(dir, filename), data);
    }
    execFileSync("git", ["add", "-f", "."], { cwd: dir });
    return spawnSync(process.execPath, [script], { cwd: dir, encoding: "utf8" });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test("accepts anonymized report, internal DNS and placeholder config", () => {
  const r = check({
    "lab/torrent-vpn/RESULT.md": "| Adresse des Hosts | anonymisiert |\n| DNS | 10.2.0.1 |\n",
    "lab/torrent-vpn/vpn/jularr.conf.example": "PrivateKey = <secret>\n"
  });
  assert.equal(r.status, 0, r.stderr);
});

test("rejects public IP and never prints the value", () => {
  const r = check({ "lab/torrent-vpn/RESULT.md": "| Adresse des Hosts | 8.8.8.8 |\n" });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /public IP in lab report/);
  assert.doesNotMatch(r.stderr, /8\.8\.8\.8/);
});

test("rejects tracked credential files and private keys", () => {
  const r = check({ "lab/torrent-vpn/vpn/vpn.env": "VPN_PASSWORD=placeholder", "test.txt": "PrivateKey = " + "A".repeat(43) + "=\n" });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /restricted credential or key filename/);
  assert.match(r.stderr, /WireGuard private key/);
  assert.doesNotMatch(r.stderr, /A{20,}/);
});

test("rejects secret key blocks but allows source templates", () => {
  const r = check({ "test.txt": "-----BEGIN " + "PRIVATE KEY-----\n", ".env.example": "JULARR_DB_PASSWORD=\n" });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /private key block/);
  assert.doesNotMatch(r.stderr, /restricted credential or key filename/);
});