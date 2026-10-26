#!/usr/bin/env node
// Fast check of committed material, not a full-history secret scanner.
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";

const forbiddenPath = /(?:^|\/)(?:\.env(?:\.(?!example$|sample$|template$)[^/]*)?|vpn\.env|jularr\.conf|id_rsa|id_ed25519|[^/]+\.(?:p12|pfx|pem|key|ovpn|kdbx|jks|keystore))$/i;
const literalSecret = [
  ["private key block", /-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----/g],
  ["credential token", /\b(?:gh[pousr]_[A-Za-z0-9]{24,}|github_pat_[A-Za-z0-9_]{24,}|AKIA[A-Z0-9]{16}|xox[baprs]-[A-Za-z0-9-]{18,}|sk-(?:proj-)?[A-Za-z0-9_-]{24,})\b/g],
  ["WireGuard private key", /\b(?:PrivateKey|WIREGUARD_PRIVATE_KEY)\s*[:=]\s*["']?[A-Za-z0-9+/]{43}=/g]
];

function publicIpv4(value) {
  const octets = value.split(".").map(Number);
  if (octets.length !== 4 || octets.some(n => n > 255)) return false;
  const [a, b, c] = octets;
  return !(a === 0 || a === 10 || a === 127 || a >= 224
    || a === 169 && b === 254 || a === 172 && b >= 16 && b <= 31
    || a === 192 && b === 168 || a === 100 && b >= 64 && b <= 127
    || a === 192 && b === 0 && c === 2
    || a === 198 && b === 51 && c === 100
    || a === 203 && b === 0 && c === 113);
}

const files = execFileSync("git", ["ls-files", "-z"], { encoding: "utf8", maxBuffer: 16 * 1024 * 1024 }).split("\0").filter(Boolean);
const problems = [];
for (const filename of files) {
  if (forbiddenPath.test(filename)) {
    problems.push(filename + ": restricted credential or key filename");
    continue;
  }

  let bytes;
  try { bytes = readFileSync(filename); } catch { problems.push(filename + ": tracked file cannot be read"); continue; }
  if (bytes.length > 4 * 1024 * 1024 || bytes.includes(0)) continue;
  const content = bytes.toString("utf8");

  for (const [reason, expression] of literalSecret) {
    expression.lastIndex = 0;
    for (const match of content.matchAll(expression)) {
      const line = content.slice(0, match.index).split("\n").length;
      problems.push(filename + ":" + line + ": " + reason);
    }
  }

  // Measured public host and VPN-exit IPs do not belong in published lab reports.
  if (filename.startsWith("lab/") && /(?:^|\/)RESULTS?\.md$/i.test(filename)) {
    const lines = content.split("\n");
    for (let i = 0; i < lines.length; i++) {
      for (const m of lines[i].matchAll(/\b(?:\d{1,3}\.){3}\d{1,3}\b/g)) {
        if (publicIpv4(m[0])) problems.push(filename + ":" + (i + 1) + ": public IP in lab report");
      }
    }
  }
}

if (problems.length) {
  console.error("Repository hygiene check failed (secret values are deliberately not printed):");
  for (const problem of problems.slice(0, 60)) console.error(" - " + problem);
  if (problems.length > 60) console.error(" - ... and " + (problems.length - 60) + " more");
  process.exitCode = 1;
} else {
  console.log("Repository hygiene check passed (" + files.length + " tracked paths).");
}