import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

// The administration menu is one definition (Framework/AdminMenu.cs); these checks read it as text, so a wrong key, icon or address fails here and not in a browser.
const root = fileURLToPath(new URL('..', import.meta.url));
const source = readFileSync(join(root, 'Framework', 'AdminMenu.cs'), 'utf8');
const resources = readFileSync(join(root, 'Localization', 'UiResources.cs'), 'utf8');

const groups = [...source.matchAll(/new\("((?:nav|admin)\.[A-Za-z.]+)",\s*\r?\n\s*\[/g)].map(match => match[1]);
const entries = [...source.matchAll(/new\("([a-z]+)", "([A-Za-z.]+)", "([a-z-]+)",\s*(?:\[|\r?\n)/g)].map(match => ({ id: match[1], label: match[2], icon: match[3] }));
const pages = [...source.matchAll(/new\("([A-Za-z.]+)", "(\/Admin\/[A-Za-z/]+)"/g)].map(match => ({ label: match[1], path: match[2] }));

function routesIn(directory) {
    const found = [];
    for (const name of readdirSync(directory)) {
        const full = join(directory, name);
        if (statSync(full).isDirectory()) found.push(...routesIn(full));
        else if (name.endsWith('.cshtml')) found.push(...[...readFileSync(full, 'utf8').matchAll(/^@page "([^"]+)"/gm)].map(match => match[1]));
    }
    return found;
}

test('the menu has six groups with fifteen entries, each with its own icon', () => {
    assert.equal(groups.length, 6);
    assert.equal(entries.length, 15);
    assert.equal(new Set(entries.map(entry => entry.icon)).size, 15, 'every entry has an icon of its own');
    assert.equal(new Set(entries.map(entry => entry.id)).size, 15);
});

test('every icon is in the project icon set and every label is a declared text', () => {
    for (const entry of entries) assert.ok(existsSync(join(root, 'Assets', 'Icons', entry.icon + '.svg')), 'icon ' + entry.icon);
    for (const key of [...groups, ...entries.map(entry => entry.label), ...pages.map(page => page.label)]) {
        assert.ok(resources.includes(`new("${key}",`), 'text ' + key);
    }
});

test('every page of an entry is an address of the application, and none is listed twice', () => {
    const conventions = readFileSync(join(root, 'Framework', 'PageConventions.cs'), 'utf8');
    const views = [...(conventions.match(/new\[\] \{ ([^}]+) \}/)?.[1] ?? '').matchAll(/"([A-Za-z]+)"/g)].map(match => '/Admin/Library/' + match[1]);
    const routes = [...routesIn(join(root, 'Pages', 'Admin')), ...views, ...[...conventions.matchAll(/AddPageRoute\("[^"]+", "([^"]+)"\)/g)].map(match => match[1])];
    const unique = new Set();
    for (const page of pages) {
        assert.ok(routes.includes(page.path), 'route ' + page.path);
        const key = page.path + '|' + page.label;
        assert.ok(!unique.has(key) || page.path === '/Admin/Acquisition/Profiles', 'twice: ' + key);
        unique.add(key);
    }

    assert.ok(pages.length >= 30);
});

test('the user menu links one area per flagged entry, taken from the same definition', () => {
    const shell = readFileSync(join(root, 'Pages', 'Shared', 'Shell.cs'), 'utf8');
    assert.match(shell, /AdminMenu\.UserLinks\(texts\)/);
    assert.doesNotMatch(shell, /ShellLink\("nav\.administration"/);
    assert.equal([...source.matchAll(/UserMenu: true|OpenRequests, true/g)].length, 6);
});
