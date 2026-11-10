#!/usr/bin/env node
// Documentation checks (docs/DOCUMENTATION_POLICY.md): links and code paths resolve, size and line limits,
// archive labels, every area file listed in the CODEMAP, no near-duplicate sentences across active documents.
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const errors = [];

// Files
function markdown(dir) {
  const full = resolve(root, dir);
  if (!existsSync(full)) return [];
  return readdirSync(full).flatMap(name => {
    const path = join(dir, name);
    return statSync(resolve(root, path)).isDirectory() ? markdown(path) : name.endsWith('.md') ? [path.split(sep).join('/')] : [];
  });
}
const archive = markdown('docs/archive');
const areas = markdown('docs/areas');
const codemaps = ['docs/CODEMAP.md', 'src/Jularr.Frontend/CODEMAP.md'];
const active = ['AGENTS.md', 'README.md', 'src/Jularr.Frontend/README.md', 'src/Jularr.Frontend/CODEMAP.md',
  ...markdown('docs').filter(f => !f.startsWith('docs/archive/') && !f.startsWith('docs/mockups/'))];
const wrapped = new Set(['AGENTS.md', 'docs/INDEX.md', 'docs/DOCUMENTATION_POLICY.md', 'docs/CONVENTIONS.md', 'docs/FRONTEND_CONVENTIONS.md',
  'docs/FRONTEND_UX.md', ...codemaps, ...areas]);
const lineLimits = { 'AGENTS.md': 120, 'docs/INDEX.md': 60, 'docs/CODEMAP.md': 80, 'src/Jularr.Frontend/CODEMAP.md': 80 };
const byteLimits = { 'docs/ANDROID_CLIENTS.md': 70000 };
const text = new Map();
const read = file => {
  if (!text.has(file)) text.set(file, existsSync(resolve(root, file)) ? readFileSync(resolve(root, file), 'utf8') : (errors.push('Missing: ' + file), ''));
  return text.get(file);
};
const prose = file => read(file).replace(/```[\s\S]*?```/g, '');

// Links and code paths
const inside = path => { const back = relative(root, path); return !(isAbsolute(back) || back === '..' || back.startsWith('..' + sep)); };
for (const file of [...active, ...archive]) {
  for (const match of prose(file).matchAll(/!?\[[^\]]*\]\((<[^>]+>|[^\s)]+)(?:\s+[^)]*)?\)/g)) {
    let target = match[1].replace(/^<|>$/g, '');
    if (/^(?:[a-z][a-z\d+.-]*:|\/|#)/i.test(target)) continue;
    target = target.split(/[?#]/)[0];
    if (!target || /[{}]/.test(target)) continue;
    try { target = decodeURIComponent(target); } catch { errors.push(`${file}: invalid link encoding ${match[1]}`); continue; }
    const full = resolve(root, dirname(file), target);
    if (!inside(full) || !existsSync(full)) errors.push(`${file}: broken link ${match[1]}`);
  }
}
const repoPath = /^(?:src|tests|database|scripts|docs|clients|\.github)\/|^Jularr\.(?:Backend|Frontend|Integrations|Workers)(?:\/|$)|^Jularr\.(?:Backend|E2E)\.Tests\//;
const placeholder = /[<>{}*?…\s]|\.\.\.|NNNN/;
const exists = (file, path) => [dirname(file), '.', 'src', 'tests'].some(base => { const full = resolve(root, base, path); return inside(full) && existsSync(full); });
for (const file of active) {
  const isMap = codemaps.includes(file);
  for (const match of prose(file).matchAll(/`([^`\n]+)`/g)) {
    const path = match[1].replace(/[.,;:]+$/, '').replace(/:\d+(?:-\d+)?$/, '').replace(/\/$/, '');
    if (placeholder.test(path) || !(repoPath.test(path) || (isMap && path.includes('/')))) continue;
    if (!exists(file, path)) errors.push(`${file}: missing code path \`${match[1]}\``);
  }
}

// Sizes and lines
for (const file of active) {
  const content = read(file), lines = content.split('\n');
  const bytes = Buffer.byteLength(content), count = lines.length - (content.endsWith('\n') ? 1 : 0);
  if (lineLimits[file] && count > lineLimits[file]) errors.push(`${file}: ${count} lines, limit ${lineLimits[file]}`);
  if (areas.includes(file)) {
    if (count > 250) errors.push(`${file}: ${count} lines, limit 250 (split the area)`);
    if (bytes > 24576) errors.push(`${file}: ${bytes} bytes, limit 24576 (split the area)`);
  } else if (bytes > (byteLimits[file] ?? 40960)) errors.push(`${file}: ${bytes} bytes, limit ${byteLimits[file] ?? 40960}`);
  if (!wrapped.has(file)) continue;
  let fenced = false;
  lines.forEach((line, i) => {
    if (line.startsWith('```')) fenced = !fenced;
    if (!fenced && !line.startsWith('|') && line.length > 160) errors.push(`${file}:${i + 1}: ${line.length} characters, limit 160`);
    if (codemaps.includes(file) && /^\s+\S/.test(line)) errors.push(`${file}:${i + 1}: CODEMAP entries are one line each`);
  });
}

// Archive labels and the area registry
for (const file of archive)
  if (!/^Status: \*\*(?:historical|snapshot)\*\*/m.test(read(file).slice(0, 1500))) errors.push(`${file}: missing Status: **historical** or **snapshot**`);
for (const file of areas)
  if (!read('docs/CODEMAP.md').includes(`(${file.replace('docs/', '')})`)) errors.push(`${file}: not listed in docs/CODEMAP.md`);

// Near-duplicate sentences across files (6-word shingles, at least 60 % of the shorter sentence)
// File pairs that may repeat a sentence on purpose, each with its reason.
const allowedDuplicates = [];
const sentences = [];
for (const file of active) {
  // Join wrapped lines; a new paragraph, list item, heading or table row starts a new block.
  const body = prose(file).replace(/^\|.*$/gm, '').replace(/\]\([^)]*\)/g, ']').replace(/\n(?!\s*\n|\s*(?:[-*+]|\d+\.) |#|\|)\s*/g, ' ');
  for (const sentence of body.split(/(?<=[.!?])\s+|\n/)) {
    const words = sentence.toLowerCase().replace(/[^\p{L}\p{N} ]+/gu, ' ').split(/\s+/).filter(Boolean);
    if (words.length < 12) continue;
    const shingles = new Set();
    for (let i = 0; i + 6 <= words.length; i++) shingles.add(words.slice(i, i + 6).join(' '));
    sentences.push({ file, sentence, shingles });
  }
}
const index = new Map();
sentences.forEach((s, i) => { for (const sh of s.shingles) { if (!index.has(sh)) index.set(sh, []); index.get(sh).push(i); } });
const shared = new Map();
for (const list of index.values()) {
  if (list.length > 20) continue;
  for (const a of list) for (const b of list) if (a < b && sentences[a].file !== sentences[b].file) shared.set(a + ',' + b, (shared.get(a + ',' + b) ?? 0) + 1);
}
for (const [key, count] of shared) {
  const [a, b] = key.split(',').map(n => sentences[n]);
  if (count / Math.min(a.shingles.size, b.shingles.size) < 0.6) continue;
  if (allowedDuplicates.some(([x, y]) => (a.file === x && b.file === y) || (a.file === y && b.file === x))) continue;
  errors.push(`Near-duplicate in ${a.file} and ${b.file}: "${a.sentence.slice(0, 100)}"`);
}

if (errors.length) { console.error(errors.join('\n')); process.exitCode = 1; }
else console.log(`Documentation checks passed (${active.length} active incl. ${areas.length} areas, ${archive.length} archived).`);
