// Run from any cwd: node src/Jularr.Frontend/Tools/verify-ui-resources.mjs
// Static guard for literal UI keys. Dynamic keys must be mapped to declared keys.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const frontend = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const catalogFile = path.join(frontend, "Localization", "UiResources.cs");
const catalog = fs.readFileSync(catalogFile, "utf8");
const declarations = [...catalog.matchAll(/new\("([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)",\s*"(?:\\.|[^"\\])*"\)/g)]
  .map(match => match[1]);
const declared = new Set(declarations);
const errors = [];

if (declarations.length === 0) errors.push("No UI source resources found.");
if (declarations.length !== declared.size) errors.push("Duplicate UI resource key declarations.");

function inspect(directory) {
  for (const item of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, item.name);
    if (item.isDirectory()) {
      if (item.name !== "obj" && item.name !== "bin" && item.name !== "Tools") inspect(full);
      continue;
    }

    if (!/\.(?:cs|cshtml)$/.test(item.name) || full === catalogFile) continue;
    const source = fs.readFileSync(full, "utf8");
    for (const match of source.matchAll(/["']([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)["']/g)) {
      // Razor Tag Helper model expressions are C# property access, not UI keys.
      if (source.slice(Math.max(0, match.index - 6), match.index).endsWith("model=")) continue;
      if (!declared.has(match[1])) {
        errors.push(`${path.relative(frontend, full)}: undeclared UI resource ${match[1]}`);
      }
    }

    if (/\bCurrentUICulture\.TwoLetterISOLanguageName\s*==/.test(source)
      || /\["(?:en|de)"\]\s*=/.test(source)) {
      errors.push(`${path.relative(frontend, full)}: hard-coded language selection`);
    }
  }
}

inspect(frontend);

if (errors.length > 0) {
  for (const error of errors) console.error(error);
  process.exitCode = 1;
} else {
  console.log(`UI resources verified: ${declared.size} source keys.`);
}
