import { copyFile, mkdir, rm } from "node:fs/promises";
import { join, resolve } from "node:path";

// Keep deployment metadata private (outside wwwroot), publish only hashed assets.
const root = resolve(import.meta.dirname, "..");
const compiled = join(root, "wwwroot", "build", ".vite");
const privateRoot = join(root, "Assets", "Generated");
await mkdir(privateRoot, { recursive: true });
for (const file of ["manifest.json", "icons.json"]) {
    await copyFile(join(compiled, file), join(privateRoot, file));
}
await rm(compiled, { recursive: true, force: true });
console.log("Private frontend asset manifests prepared.");
