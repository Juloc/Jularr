// Writes the shared design tokens into the stylesheet the frontend compiles. The JSON next to
// this script is the one source: the web stylesheet, the runtime palette and the native
// clients all read it, so no other place may carry a color of the skin.
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const tokens = JSON.parse(readFileSync(join(root, "Assets", "Shared", "tokens.json"), "utf8"));
const output = join(root, "Assets", "Generated", "tokens.generated.scss");

function skin(name) {
    const definition = tokens.skins[name];
    const light = definition.palette.light;
    const dark = definition.palette.dark;
    const pair = (key) => `light-dark(${light[key]}, ${dark[key]})`;
    return `
    --background: ${pair("background")};
    --surface: ${pair("surface")};
    --surface-hover: color-mix(in srgb, var(--accent) 7%, var(--surface));
    --field: ${pair("field")};
    --border: ${pair("border")};
    --text: ${pair("text")};
    --muted: ${pair("muted")};
    --accent: ${light.accent};
    --accent-contrast: ${light.accentContrast};
    --accent-text: ${pair("accentText")};
    --accent-hover: ${light.accentHover};
    --danger: ${pair("danger")};
    --success: ${pair("success")};
    --shadow: ${definition.shadow};
    --radius-control: ${tokens.radius.control}px;
    --radius-panel: ${tokens.radius.panel}px;
    --focus: var(--accent-text);
    --accent-soft: color-mix(in srgb, var(--accent) 12%, var(--surface));
    // Outlines of panels and table heads: the border with a touch of the accent.
    --accent-line: color-mix(in srgb, var(--accent) 24%, var(--border));
    --jularr-ring: var(--accent);
    --jularr-ink: var(--text);
`;
}

const stylesheet = `// Generated from Assets/Shared/tokens.json by Tools/tokens.mjs. Do not edit.
:root {
    font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
    color-scheme: light;${skin("clean")}
    --art-hue-rotate: 0deg;
    --art-saturate: 1;
}

html[data-skin="original"] {${skin("original")}
    --paper-opacity: .55;
    --paper-blend: multiply;
    --paper-filter: none;
    --landscape-opacity: .28;
    --blossom-opacity: .55;
    --art-night-filter: brightness(1);
}
`;

mkdirSync(dirname(output), { recursive: true });
writeFileSync(output, stylesheet, "utf8");
console.log("tokens: " + Object.keys(tokens.skins.clean.palette.light).length + " palette values, " + Object.keys(tokens.status).length + " status values");
