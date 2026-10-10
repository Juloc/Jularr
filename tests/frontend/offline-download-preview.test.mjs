import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const source = await readFile(new URL('../../src/Jularr.Web/wwwroot/js/offline-library-ui.js', import.meta.url), 'utf8');

class Element {
  children = [];
  listeners = new Map();
  hidden = false;
  open = false;
  textContent = '';
  append(...elements) { this.children.push(...elements); }
  replaceChildren(...elements) { this.children = elements; }
  setAttribute(name, value) { this[name] = value; }
  addEventListener(name, callback) { this.listeners.set(name, callback); }
  dispatchEvent(event) { this.listeners.get(event.type)?.(event); }
  matches() { return this.open; }
}

async function harness(books, media, fail = false) {
  const root = new Element();
  const nodes = new Map(['list', 'loading', 'error'].map(name => [`[data-offline-preview-${name}]`, new Element()]));
  nodes.set('[data-offline-preview-icon]', { content: { cloneNode: () => new Element() } });
  root.querySelector = selector => nodes.get(selector);
  const badge = new Element();
  const changes = [];
  const events = new Map();
  const document = {
    documentElement: { lang: 'en' },
    readyState: 'complete',
    getElementById: () => ({ textContent: JSON.stringify({
      'offlineLibrary.settings.empty': 'No offline downloads.',
      'offlineLibrary.action.available': 'Available offline',
      'offlineLibrary.action.downloading': 'Downloading…',
      'offlineLibrary.action.failed': 'Failed'
    }) }),
    querySelector: () => badge,
    querySelectorAll: selector => selector === '[data-offline-download-preview]' ? [root] : [],
    createElement: tag => Object.assign(new Element(), { tag })
  };
  const context = {
    document, Event,
    window: {
      addEventListener: (name, callback) => events.set(name, callback),
      JularrOfflineLibraryManager: { getSharedManager: async () => ({
        listBooks: async () => { if (fail) throw new Error('IndexedDB unavailable'); return books; },
        onChange: callback => changes.push(callback)
      }) },
      JularrOfflineMediaManager: { packages: async () => media },
      JularrOfflineMedia: { CHUNK_BYTES: 100, chunkCount: size => Math.ceil(size / 100) }
    }
  };
  vm.runInNewContext(source, context);
  const settle = () => new Promise(resolve => setImmediate(resolve));
  await settle();
  root.open = true;
  root.dispatchEvent({ type: 'toggle', newState: 'open' });
  await settle();
  return { root, nodes, badge, changes, events, settle };
}

test('combines real offline reading and playback state; bounds the preview and never calls acquisition', async () => {
  const books = Array.from({ length: 8 }, (_, index) => ({ manifest: { title: `Book ${index}` }, status: 'available', updatedAt: index }));
  const media = [{ title: '<script>Movie</script>', state: 'downloading', updatedAt: '2026-10-09', sizeBytes: 250,
    resources: [{ sizeBytes: 250, completedChunks: 1 }] }];
  const { nodes, badge, events, settle } = await harness(books, media);
  const rows = nodes.get('[data-offline-preview-list]').children;
  assert.equal(rows.length, 6);
  const content = rows[0].children[1];
  assert.equal(content.children[0].textContent, '<script>Movie</script>');
  assert.equal(content.children[0].href, '/Settings/Offline');
  assert.equal(content.children.at(-1).value, 100);
  assert.equal(content.children.at(-1).max, 250);
  assert.equal(badge.textContent, '1');
  media[0].state = 'ready';
  events.get('jularr:offline-media-ready')();
  await settle();
  assert.equal(badge.hidden, true);
});

test('shows no invented progress for unknown sizes and preserves failure states', async () => {
  const { nodes } = await harness([], [{ title: 'Audio', state: 'downloading' }, { title: 'Novel', state: 'failed' }]);
  const rows = nodes.get('[data-offline-preview-list]').children;
  assert.equal(rows[0].children[1].children.length, 2);
  assert.equal(rows[1].children[1].children[1].textContent, 'Failed');
});

test('empty and inaccessible device storage have distinct truthful states', async () => {
  const empty = await harness([], []);
  assert.equal(empty.nodes.get('[data-offline-preview-list]').children[0].textContent, 'No offline downloads.');
  assert.equal(empty.badge.hidden, true);
  const failed = await harness([], [], true);
  assert.equal(failed.nodes.get('[data-offline-preview-error]').hidden, false);
  assert.equal(failed.nodes.get('[data-offline-preview-list]').children.length, 0);
});
