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

// Service error texts are declared by the backend and shown by pages under the same keys.
const serviceErrors = path.resolve(frontend, "..", "Jularr.Backend", "Service", "Core", "Localization", "ServiceErrorResources.cs");
if (fs.existsSync(serviceErrors)) for (const match of fs.readFileSync(serviceErrors, "utf8").matchAll(/new\("(errors\.[A-Za-z0-9_.-]+)",/g)) declared.add(match[1]);

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
      // Backend permission identifiers are not visible UI text.
      if (/(?:\.Permissions\.Contains|\.Can|\bRight)\(\s*$/.test(source.slice(Math.max(0, match.index - 50), match.index))) continue;
      // Asset/file extensions are not localization keys.
      if (/\.(?:json|css|scss|js|mjs|ts|svg|png|webp|avif|cs|cshtml)$/.test(match[1])) continue;
      if (!declared.has(match[1])) {
        errors.push(`${path.relative(frontend, full)}: undeclared UI resource ${match[1]}`);
      }
    }


    // Visible Razor literal prose bypasses the DB catalog. Brand glyphs are not language strings.
    if (item.name.endsWith(".cshtml")) {
      for (const match of source.matchAll(/>([^<>\n]+)</g)) {
        const literal = match[1].trim();
        if (literal && /[a-z]/i.test(literal) && !literal.includes("@") && literal !== "J") {
          errors.push(`${path.relative(frontend, full)}: raw visible Razor text ${literal}`);
        }
      }
    }

    // Pages use the Topbar breadcrumb as their only page title.
    if (full.includes(path.sep + "Pages" + path.sep)) {
      if (/new\s+UiHeading\s*\(/.test(source) || /new\s+UiTable\s*\(/.test(source)) {
        errors.push(`${path.relative(frontend, full)}: redundant heading or headings-only table`);
      }
      if (item.name.endsWith(".cshtml") && !full.includes(path.sep + "Account" + path.sep)
          && /<h1(?:\s|>)/i.test(source)) {
        errors.push(`${path.relative(frontend, full)}: page title belongs in Topbar`);
      }
      if (/(?:UiScope|DiscoverScope)\s*\(\s*\[\s*["']media\./.test(source)) {
        errors.push(`${path.relative(frontend, full)}: media scopes must be capability-filtered`);
      }
    }

    if (/\bCurrentUICulture\.TwoLetterISOLanguageName\s*==/.test(source)
      || /\["(?:en|de)"\]\s*=/.test(source)) {
      errors.push(`${path.relative(frontend, full)}: hard-coded language selection`);
    }
  }
}

inspect(frontend);

// Release decisions name their reason in the Backend; each one is shown as "acquisition.reason.<code>".
const acquisition = path.resolve(frontend, "..", "Jularr.Backend", "Logic", "Acquisition");
for (const name of fs.readdirSync(acquisition, { recursive: true }).filter(file => file.endsWith(".cs"))) {
  const source = fs.readFileSync(path.join(acquisition, name), "utf8");
  for (const match of source.matchAll(/\b(?:Conflict|Ambiguous|Exact|Strong|Fit|ReleaseReason|Reason)\("([A-Z][A-Za-z]+)"/g)) {
    if (!declared.has("acquisition.reason." + match[1])) {
      errors.push(`Jularr.Backend/Logic/Acquisition/${name}: reason ${match[1]} has no acquisition.reason text`);
    }
  }
}

if (errors.length > 0) {
  for (const error of errors) console.error(error);
  process.exitCode = 1;
} else {
  console.log(`UI resources verified: ${declared.size} source keys.`);
}
