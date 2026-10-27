#!/usr/bin/env node
import { existsSync, readFileSync } from 'node:fs';
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const active = ['AGENTS.md', 'README.md', 'docs/DOCUMENTATION_POLICY.md', 'docs/CONVENTIONS.md', 'docs/FRONTEND_CONVENTIONS.md', 'docs/FRONTEND_UX.md', 'docs/CODEMAP.md', 'docs/INSTANCE_SETUP.md', 'docs/STORAGE_RUNTIME.md', 'src/Jularr.Frontend/CODEMAP.md'];
const historical = ['docs/ALPHA90_RECOVERY_PLAN.md', 'docs/BACKEND_NEXT.md', 'docs/COMPLETION_PLAN.md', 'docs/FEATURE_MATRIX.md'];
const errors = [];
function contents(file) {
  const path = resolve(root, file);
  if (!existsSync(path)) { errors.push('Missing documentation: ' + file); return ''; }
  return readFileSync(path, 'utf8');
}
for (const file of active) {
  const markdown = contents(file).replace(/```[\s\S]*?```/g, '');
  for (const match of markdown.matchAll(/!?\[[^\]]*\]\((<[^>]+>|[^\s)]+)(?:\s+[^)]*)?\)/g)) {
    let target = match[1].replace(/^<|>$/g, '');
    if (/^(?:[a-z][a-z\d+.-]*:|\/|#)/i.test(target)) continue;
    target = target.split(/[?#]/)[0];
    if (!target || target.includes('{') || target.includes('}')) continue;
    try { target = decodeURIComponent(target); }
    catch { errors.push('Invalid link encoding in ' + file + ': ' + match[1]); continue; }
    const full = resolve(root, dirname(file), target);
    const back = relative(root, full);
    if (isAbsolute(back) || back === '..' || back.startsWith('..' + sep) || !existsSync(full))
      errors.push('Broken local link in ' + file + ': ' + match[1]);
  }
}
for (const file of historical) {
  if (!/^Status: \*\*(?:historical|snapshot)\*\*/m.test(contents(file).slice(0, 1500)))
    errors.push('Missing historical/snapshot label: ' + file);
}
if (contents('docs/CODEMAP.md').includes('## Next slices (not implemented)')) errors.push('CODEMAP contains obsolete next slices');
if (contents('README.md').includes('[vier Schritte, Presets und Owner]')) errors.push('README still describes old setup');
if (contents('docs/ALPHA90_RECOVERY_PLAN.md').includes('**Current hard gap:**')) errors.push('Old Alpha-90 gap still claims current status');
if (errors.length) { console.error(errors.join('\n')); process.exitCode = 1; }
else console.log('Documentation checks passed (' + active.length + ' active, ' + historical.length + ' historical).');
