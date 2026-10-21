// Preserve original notices beside the delivered binary and identify its matching public source snapshot.
import { execFileSync } from 'node:child_process';
import { readFile, readdir, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const runtime = join(root, 'Modules/Speech/Runtime');
const declared = process.env.JULARR_BROWSER_SOURCE_REVISION;
if (declared && !/^[a-f0-9]{40}$/.test(declared)) throw new Error('JULARR_BROWSER_SOURCE_REVISION must be an exact Git commit.');
let revision;
let development = !declared;
try {
    revision = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    development = !!execFileSync('git', ['status', '--porcelain'], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
} catch { /* A source archive can identify its revision explicitly; an unidentified build stays labelled development-only. */ }
if (revision && declared && revision !== declared) throw new Error('Declared browser source revision differs from the checkout.');
revision ??= declared;
const source = revision && /^[a-f0-9]{40}$/.test(revision) ? 'https://github.com/Juloc/Jularr/tree/' + revision : 'https://github.com/Juloc/Jularr';
const files = [join(root, 'BROWSER-LICENSE.md'), join(runtime, 'NOTICE.md'), join(runtime, 'seek-bzip/LICENSE'),
    ...((await readdir(join(runtime, 'licenses'))).sort().map(file => join(runtime, 'licenses', file))),
    join(root, 'node_modules/hls.js/LICENSE'), join(root, 'node_modules/htmx.org/LICENSE'), join(root, 'Modules/Reader/Translation/LICENSE'),
    ...['LICENSE', 'wasm/LICENSE_OPENJPEG', 'wasm/LICENSE_JBIG2', 'wasm/LICENSE_QCMS'].map(file => join(root, 'node_modules/pdfjs-dist', file))];
const sections = await Promise.all(files.map(async file => file.slice(root.length + 1).replaceAll('\\', '/') + '\n\n' + await readFile(file, 'utf8')));
await writeFile(join(runtime, 'NOTICE.generated.txt'), 'Jularr browser-client source and third-party notices\n\n'
    + (development ? 'DEVELOPMENT BUILD: uncommitted/unversioned changes are not covered by the source snapshot below. Do not publish this as a matching-source release.\n' : 'Matching public browser-client source snapshot:\n')
    + source + '\n\nRetain matching sources and build instructions when redistributing. Server/project licensing is not changed by the browser-client grant.\n\n'
    + sections.join('\n\n--------------------\n\n'));
console.log('Browser/Speech license bundle prepared (' + (development ? 'development' : revision) + ').');
