import { createHash } from "node:crypto";
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { join, relative, resolve, extname } from "node:path";
import SVGSpriter from "svg-sprite";

const root = resolve(import.meta.dirname, "..");
const dest = join(root, "wwwroot", "build");
const icons = [];

// Shared, page and module SVGs remain beside their owners in source control.
async function visit(folder) {
    for (const entry of await readdir(folder, { withFileTypes: true })) {
        const file = join(folder, entry.name);
        if (entry.isDirectory()) await visit(file);
        else if (extname(entry.name).toLowerCase() === ".svg") icons.push(file);
    }
}

for (const folder of ["Assets/Icons", "Assets/Flags", "Modules", "Pages", "Framework/Components"]) {
    const file = join(root, folder);
    try { await visit(file); }
    catch (error) { if (error.code !== "ENOENT") throw error; }
}

if (icons.length === 0) throw new Error("No frontend SVG icons found.");
const symbols = {};
const paths = new Set();
const spriter = new SVGSpriter({
    dest,
    shape: {
        id: {
            generator: file => {
                const relativeName = relative(root, file).replaceAll("\\", "/")
                    .replace(/^Assets\/Icons\//, "").replace(/\.svg$/i, "");
                const name = relativeName.replace(/[^a-zA-Z0-9]+/g, "-").toLowerCase();
                if (paths.has(name)) throw new Error("Duplicate icon symbol: " + name);
                paths.add(name);
                symbols[name] = "icon-" + name;
                return "icon-" + name;
            }
        }
    },
    mode: { symbol: { dest: ".", sprite: "icons.svg" } }
});

for (const file of icons.sort()) spriter.add(file, null, await readFile(file, "utf8"));

const result = await new Promise((accept, reject) =>
    spriter.compile((error, output) => error ? reject(error) : accept(output)));
const sprite = result.symbol.sprite.contents;
const hash = createHash("sha256").update(sprite).digest("hex").slice(0, 16);
const file = "assets/icons-" + hash + ".svg";
await mkdir(join(dest, "assets"), { recursive: true });
await mkdir(join(dest, ".vite"), { recursive: true });
await writeFile(join(dest, file), sprite);
await writeFile(join(dest, ".vite", "icons.json"), JSON.stringify({ file, symbols }, null, 2));
console.log("SVG sprite: " + file + " (" + Object.keys(symbols).length + " symbols)");
