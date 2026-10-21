import { cp, mkdir, readdir, rm } from "node:fs/promises";
import { join, resolve } from "node:path";

// Mozilla's pdf.js reads the original pages in the browser. Besides its script and worker, which the build bundles with fingerprints, it loads the decoders for JPEG 2000 and JBIG2 scans,
// the predefined character maps, the standard fonts and the colour profile by address. They are copied from the pinned npm package beside the build, with their licenses, so nothing comes from a CDN.
const root = resolve(import.meta.dirname, "..");
const source = join(root, "node_modules", "pdfjs-dist");
const output = join(root, "wwwroot", "pdfjs");

await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });
await cp(join(source, "LICENSE"), join(output, "LICENSE"));
for (const folder of ["cmaps", "standard_fonts", "iccs"]) await cp(join(source, folder), join(output, folder), { recursive: true });

// The scripting sandbox is not used; the decoders and the colour module are.
await mkdir(join(output, "wasm"), { recursive: true });
for (const file of await readdir(join(source, "wasm"))) {
    if (!file.startsWith("quickjs")) await cp(join(source, "wasm", file), join(output, "wasm", file));
}

console.log("pdf.js decoders, character maps and fonts copied.");
