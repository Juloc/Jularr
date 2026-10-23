import { createHash } from "node:crypto";
import { copyFile, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, join, normalize, resolve, sep } from "node:path";
import { gunzipSync } from "node:zlib";

// EmulatorJS and the cores Jularr plays, pinned by version and npm integrity. Only the browser files are kept;
// the npm packages themselves would pull every core and the EmulatorJS server tooling.
export const Version = "4.2.3";
export const Packages = [
    ["emulatorjs", "sha512-7z3qaA4LwyurhuGvdMUDF9xJpEbxC3SNy9+E9tSaOsRo8FCS2QXam/0k/lc9kqHWRFIlLKWahNjPAStyL0rFnw=="],
    ["core-fceumm", "sha512-XX9Vv2N/hzp0TstNMCTSppEs+sg+1lpJpPdSDuRqIO/cwdt7dUcF+WjNX1yQJLRbP5+XwcNHZ6K4BKy8CJpndQ=="],
    ["core-snes9x", "sha512-JTe9Rv9eOuCkgkG4ILwuCZUqNcVI3m9ju1NHTTjridUlFgJSqL4DUqF/vLKN9olCpsqlJFfcCayAOoikJxefwA=="],
    ["core-mupen64plus_next", "sha512-HnnEXbOEpYxD7f2wUJQLxWUpEB0bT2kC56u5s7PLyj/xNI5ckQKka5+ay1QhU2fOv2BuPRwjjhlzmVJmk5e3hw=="],
    ["core-gambatte", "sha512-Oq3AJL9SKnMsNAmtAP2eN934TQts3NHeaAe0z1BO9Lx/L3xDZKLpxL+XwgcSXIrl4Sx1oXxYEUwD7JjPEQ0DGg=="],
    ["core-mgba", "sha512-daiHzZQKEr+P9fra7j5YoEAXiyYUEtBhFQ8EAV/SeCtrkvqtayU7GQ9LYgoSgzkKSwsbNSskApqGuA9EGARYPA=="],
    ["core-melonds", "sha512-IjsEBNLPPbqU2GSeWEoEkOgAonK3JEyzvoYR3ucgOXu6InzDNdfuA5kP/mMh3d6DwCUbOwfnqGZuZXMTyNE2wA=="],
    ["core-beetle_vb", "sha512-/e6qkA/spYw27qDmzxMiMW5ic36N5W1AVU3Qki8BTZGWKXDseZtNuV/zlPts9rwtTuH8k4+fsQLF+dHp905+/Q=="],
    ["core-genesis_plus_gx", "sha512-D1e2XA2CPRAjfr0JurpXJgR9dePxl/xHtaE8v1T9BqD9A3DLfobIFWsWU9jCFS4NfUxCvcLdTYdY/We3QzvcTw=="],
    ["core-stella2014", "sha512-bs5az26pMrM6jswhZ5YxBXLpIwclwfdnWVbaEpip7yjJFQHactL7KYH7hVLts0JWDnfEK2lNxh4zlTfI6HAHmQ=="],
    ["core-handy", "sha512-miv2nSSVIIHFcGeEdeO7BpYKsljL1j+0an4BO7xw44s9iUp2PZnHiY1mHWUIOzf4o22VuiXd/TvHOKUGaEYMNw=="],
    ["core-mednafen_pce", "sha512-PwnQAKKxWT8m8dUscH4VX8jcClvgxgR/NkE6FxNE8qiRDyBH3Jze7+tF9+UdWLoJRBwssR5knd7o/Es9xIlU6A=="],
    ["core-mednafen_ngp", "sha512-q/BhrhIk1+yGQ8zXPHaRXMm7Bqgu+7RkRTwbnUSlH3E/ybj8npA3SOP8tvA8TK2ceZU2F5pWdDAwp3dzR3iikg=="],
    ["core-mednafen_wswan", "sha512-ePj0OoSTErXS171PCUsiZl3OrfPJVwupvRYFiMSzu6K2UaQjK3ESThWf8Rh1niJt1F1OQyjQENAeElSm0IcojQ=="]
];

// The loader's script order; one bundle replaces the minified build the npm package lacks.
const Scripts = ["emulator.js", "nipplejs.js", "shaders.js", "storage.js", "gamepad.js", "GameManager.js", "socket.io.min.js", "compression.js"];
const Marker = Version + " bundled";

// Regular files of an uncompressed tar archive; long names use the ustar prefix.
export function* tarEntries(archive) {
    for (let offset = 0; offset + 512 <= archive.length;) {
        const header = archive.subarray(offset, offset + 512);
        if (header.every(byte => byte === 0)) return;
        const text = (start, length) => header.subarray(start, start + length).toString("utf8").replace(/\0.*$/s, "");
        const size = parseInt(text(124, 12).trim() || "0", 8);
        const prefix = text(345, 155);
        const type = text(156, 1);
        const start = offset + 512;
        if (type === "" || type === "0") yield { name: (prefix ? prefix + "/" : "") + text(0, 100), bytes: archive.subarray(start, start + size) };
        offset = start + Math.ceil(size / 512) * 512;
    }
}

// Loader files keep their place below data/; core files go to data/cores/.
export function target(packageName, entryName) {
    if (!entryName.startsWith("package/")) return null;
    const path = entryName.slice("package/".length);
    if (path.split("/").some(part => part === ".." || part === "." || part === "")) return null;
    if (packageName === "emulatorjs") return path === "LICENSE" ? "LICENSE" : path.startsWith("data/") ? path : null;
    return /^[a-z0-9_]+(-thread)?(-legacy)?-wasm\.data$/.test(path) || /^reports\/[a-z0-9_]+\.json$/.test(path) ? "data/cores/" + path : null;
}

async function install(output) {
    const marker = join(output, "version.txt");
    if (await readFile(marker, "utf8").catch(() => "") === Marker) return false;

    await rm(output, { recursive: true, force: true });
    for (const [name, integrity] of Packages) {
        const response = await fetch(`https://registry.npmjs.org/@emulatorjs/${name}/-/${name}-${Version}.tgz`);
        if (!response.ok) throw new Error(`@emulatorjs/${name} could not be downloaded (${response.status}).`);
        const archive = Buffer.from(await response.arrayBuffer());
        if ("sha512-" + createHash("sha512").update(archive).digest("base64") !== integrity) throw new Error(`@emulatorjs/${name} does not match its pinned integrity.`);

        for (const entry of tarEntries(gunzipSync(archive))) {
            const path = target(name, entry.name);
            if (!path) continue;
            const file = normalize(join(output, path));
            if (!file.startsWith(output + sep)) throw new Error(`@emulatorjs/${name} contains an unsafe path.`);
            await mkdir(dirname(file), { recursive: true });
            await writeFile(file, entry.bytes);
        }
    }

    const data = join(output, "data");
    const sources = await Promise.all(Scripts.map(script => readFile(join(data, "src", script), "utf8")));
    await writeFile(join(data, "emulator.min.js"), sources.join("\n;\n"));
    await copyFile(join(data, "emulator.css"), join(data, "emulator.min.css"));
    await writeFile(marker, Marker);
    return true;
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(import.meta.filename)) {
    const output = resolve(import.meta.dirname, "..", "wwwroot", "emulator");
    console.log(await install(output) ? `EmulatorJS ${Version} installed.` : `EmulatorJS ${Version} is present.`);
}
