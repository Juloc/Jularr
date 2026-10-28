import { readdirSync } from "node:fs";
import { join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

const root = fileURLToPath(new URL(".", import.meta.url));

// Files live beside the owning Page, Module or Component.
// Only *.entry.ts and *.entry.scss are independent entry points.
function findEntries(folder: string): Record<string, string> {
    const result: Record<string, string> = {};
    for (const entry of readdirSync(folder, { withFileTypes: true })) {
        const full = join(folder, entry.name);
        if (entry.isDirectory()) {
            if (!["node_modules", "wwwroot", "obj", "bin"].includes(entry.name)) {
                Object.assign(result, findEntries(full));
            }
        } else if (/\.entry\.(?:ts|scss)$/.test(entry.name)) {
            const relativeName = relative(root, full).split(sep).join("/");
            // Keep CSS/TS pairs distinct.
            result[relativeName] = full;
        }
    }
    return result;
}

const input = findEntries(root);
if (!("Assets/Shared/shell.entry.scss" in input) || !("Modules/Appearance/theme.entry.ts" in input)) {
    throw new Error("Shared shell CSS and appearance script entries are required.");
}

export default defineConfig({
    root,
    // Match the host asset root.
    base: "/build/",
    publicDir: false,
    // Module workers (the subtitle renderer's own worker splits into chunks).
    worker: { format: "es" },
    build: {
        outDir: resolve(root, "wwwroot/build"),
        emptyOutDir: true,
        manifest: true,
        assetsDir: "assets",
        // Browser and home-screen icons are linked by the layout, so they must stay files, never data URIs.
        assetsInlineLimit: (file) => (/Assets\/Shared\/(?:favicon|icon-|apple-touch)/.test(file.replaceAll("\\", "/")) ? false : undefined),
        rollupOptions: { input },
        cssCodeSplit: true
    }
});
