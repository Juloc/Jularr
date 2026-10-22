import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

// The user navigation (the shell's links and the administration areas the user menu adds) gives every entry an icon of its own, from the project icon set.
const root = fileURLToPath(new URL('..', import.meta.url));
const shell = readFileSync(join(root, 'Pages', 'Shared', 'Shell.cs'), 'utf8');
const menu = readFileSync(join(root, 'Framework', 'AdminMenu.cs'), 'utf8');

const links = [...shell.matchAll(/new(?: ShellLink)?\("([A-Za-z.]+)", "(\/[A-Za-z/]*)", "([a-z-]+)"\)/g)].map(match => ({ label: match[1], icon: match[3] }));
const starts = [...menu.matchAll(/new\("([a-z]+)", "([A-Za-z.]+)", "([a-z-]+)",\s*(?:\[|\r?\n)/g)];
const areas = starts
    .map((start, index) => ({ label: start[2], icon: start[3], text: menu.slice(start.index, starts[index + 1]?.index ?? menu.length) }))
    .filter(entry => /UserMenu: true|OpenRequests, true\)/.test(entry.text));

test('the shell and the user menu name their links', () => {
    assert.ok(links.length === 7, 'shell links: ' + links.length);
    assert.equal(areas.length, 6);
});

test('every entry of the user navigation has its own icon', () => {
    const entries = [...links, ...areas];
    const seen = new Map();
    for (const entry of entries) {
        assert.ok(!seen.has(entry.icon), `icon ${entry.icon} is used by ${seen.get(entry.icon)} and by ${entry.label}`);
        seen.set(entry.icon, entry.label);
    }
});

test('every icon of the user navigation is in the project icon set', () => {
    for (const entry of [...links, ...areas]) assert.ok(existsSync(join(root, 'Assets', 'Icons', entry.icon + '.svg')), 'icon ' + entry.icon);
});
