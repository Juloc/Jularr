import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

// The German catalog covers every text the setup steps, the folder browser and the storage pages show.
const frontend = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const unescape = text => { try { return JSON.parse('"' + text + '"'); } catch { return text; } };
const declared = new Map([...fs.readFileSync(path.join(frontend, "Localization", "UiResources.cs"), "utf8")
    .matchAll(/new\("([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)",\s*"((?:\\.|[^"\\])*)"\)/g)].map(match => [match[1], unescape(match[2])]));
const german = JSON.parse(fs.readFileSync(path.join(frontend, "Localization", "Catalogs", "de.json"), "utf8").replace(/^\uFEFF/, "")).texts;

// Words that read the same in German.
const sameInGerman = new Set(["Name", "Online", "Offline", "Synology", "Jularr", "Wake-on-LAN", "Anime", "Manga", "Games", "Start", "Optional", "Server", "Port", "SABnzbd", "Minute", "Admin", "Status", "Plex", "TMDB", "NFS", "SMB"]);

function sources(directory) {
    return fs.readdirSync(directory, { withFileTypes: true }).flatMap(item => item.isDirectory() ? sources(path.join(directory, item.name))
        : /\.(?:cs|cshtml)$/.test(item.name) ? [path.join(directory, item.name)] : []);
}

test("every text of the setup, the folder browser and the storage pages has a German text", () => {
    const files = [...sources(path.join(frontend, "Pages", "Setup")), ...sources(path.join(frontend, "Pages", "Admin", "Storage")), ...sources(path.join(frontend, "Pages", "Admin", "DownloadStorage")),
        ...["FolderPicker.cs", "StorageBrowser.cs", "Modal.cs"].map(name => path.join(frontend, "Framework", "Components", name))];
    const keys = new Set([...declared.keys()].filter(key => /^(?:setup|storage|folderPicker|downloadStorage)\./.test(key)));
    for (const file of files) {
        for (const match of fs.readFileSync(file, "utf8").matchAll(/["']([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)["']/g)) if (declared.has(match[1])) keys.add(match[1]);
    }

    assert.ok(keys.size > 100, "The pages should use many texts.");
    const problems = [];
    for (const key of [...keys].sort()) {
        const entry = german[key];
        if (!entry) problems.push(`${key}: no German text`);
        else if (entry[1] === declared.get(key) && !sameInGerman.has(entry[1]) && !/^(?:https?:|\{0\})/.test(entry[1])) problems.push(`${key}: still English (${entry[1]})`);
    }

    assert.deepEqual(problems, []);
});
