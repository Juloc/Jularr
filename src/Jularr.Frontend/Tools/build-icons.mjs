import { createHash } from "node:crypto";
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { extname, join, relative, resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const dest = join(root, "wwwroot", "build");
const icons = [];

async function visit(folder) {
    for (const entry of await readdir(folder, { withFileTypes: true })) {
        const file = join(folder, entry.name);
        if (entry.isDirectory()) await visit(file);
        else if (extname(entry.name).toLowerCase() === ".svg") icons.push(file);
    }
}

for (const folder of ["Assets/Icons", "Modules", "Pages", "Framework/Components"]) {
    const path = join(root, folder);
    try { await visit(path); }
    catch (error) { if (error.code !== "ENOENT") throw error; }
}

if (icons.length === 0) throw new Error("No frontend SVG icons found.");

const symbols = {};
const shapes = [];

for (const file of icons.sort()) {
    const relativeName = relative(root, file).replaceAll("\\", "/")
        .replace(/^Assets\/Icons\//, "").replace(/\.svg$/i, "");
    const name = relativeName.replace(/[^a-zA-Z0-9]+/g, "-").toLowerCase();
    if (symbols[name]) throw new Error("Duplicate icon symbol: " + name);

    const svg = (await readFile(file, "utf8")).trim();
    const parts = /^<svg\b([^>]*)>([\s\S]*?)<\/svg>$/.exec(svg);
    if (!parts || /<script\b|<!DOCTYPE|\son\w+\s*=/i.test(svg)) {
        throw new Error("Invalid frontend SVG: " + relativeName);
    }

    const attributes = parts[1].replace(/\s+xmlns(?::\w+)?="[^"]*"/g, "");
    const id = "icon-" + name;
    symbols[name] = id;
    shapes.push(`<symbol id="${id}"${attributes}>${parts[2]}</symbol>`);
}

const sprite = `<svg xmlns="http://www.w3.org/2000/svg">${shapes.join("")}</svg>`;
const hash = createHash("sha256").update(sprite).digest("hex").slice(0, 16);
const output = "assets/icons-" + hash + ".svg";

await mkdir(join(dest, "assets"), { recursive: true });
await mkdir(join(dest, ".vite"), { recursive: true });
await writeFile(join(dest, output), sprite);
await writeFile(join(dest, ".vite", "icons.json"), JSON.stringify({ file: output, symbols }, null, 2));
console.log("SVG sprite: " + output + " (" + shapes.length + " symbols)");
