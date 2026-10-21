interface Color { l: number; c: number; h: number }
type Rgb = [number, number, number];
type Tokens = Record<string, string>;

export const OriginalAccent = "#c8102e";
export const CleanAccent = "#7c3aed";

// One seed, two accessible palettes.
export function buildPalette(seed: string): { light: Tokens; dark: Tokens; rotation: string; saturation: string } {
    if (!/^#[a-f\d]{6}$/i.test(seed)) throw new RangeError("Expected a six-digit hex accent.");
    const color = fromHex(seed);
    const monochrome = color.c < 0.025;
    const rotation = ((hue(seed) - hue(OriginalAccent)) % 360 + 540) % 360 - 180;
    return {
        light: mode(color, monochrome, false), dark: mode(color, monochrome, true),
        rotation: (monochrome ? 0 : rotation).toFixed(1) + "deg", saturation: monochrome ? "0" : "1"
    };
}

function mode(seed: Color, monochrome: boolean, dark: boolean): Tokens {
    // Tint neutral surfaces gently.
    const chroma = monochrome ? 0 : Math.min(0.25, Math.max(0.09, seed.c));
    const neutral = monochrome ? 0 : Math.min(0.007, chroma * 0.035);
    const color = (l: number, c = neutral) => toHex({ l, c, h: seed.h });
    // Owner's decision 20 Oct: light surfaces are plain white, never grey; hover is an accent tint.
    const white = (l: number) => color(l, 0);
    const background = dark ? color(0.165) : white(1);
    const surface = dark ? color(0.205) : white(1);
    const hover = dark ? color(0.235) : color(0.965, Math.min(chroma * 0.1, 0.02));
    const field = dark ? color(0.18) : white(1);
    const grounds = [background, surface, hover, field];
    const direction = dark ? 0.01 : -0.01;
    const readable = (l: number, c: number, target: number, backgrounds = grounds) => ensureContrast({ l, c, h: seed.h }, backgrounds, target, direction);

    // Keep actions and labels readable.
    let solid: Color = {
        l: monochrome ? (dark ? 0.93 : 0.22) : Math.min(dark ? 0.80 : 0.82, Math.max(dark ? 0.50 : 0.40, seed.l)),
        c: chroma, h: seed.h
    };
    const ink = color(0.18, Math.min(chroma * 0.2, 0.02));
    let accent = toHex(solid);
    let label = "#ffffff";
    for (let step = 0; step < 60; step++) {
        accent = toHex(solid);
        if (contrast(accent, "#ffffff") >= 4.5) break;
        if (contrast(accent, ink) >= 4.5) { label = ink; break; }
        solid = { ...solid, l: solid.l + (solid.l >= 0.62 ? 0.01 : -0.01) };
    }
    const soft = color(dark ? 0.285 : 0.935, Math.min(chroma * (dark ? 0.35 : 0.22), dark ? 0.06 : 0.04));
    const accentText = readable(monochrome ? (dark ? 0.90 : 0.25) : (dark ? Math.max(solid.l, 0.74) : Math.min(solid.l, 0.50)), chroma * 0.85, 4.5, [...grounds, soft]);
    const accentHover = toHex({ ...solid, l: solid.l + (label === "#ffffff" ? -0.05 : 0.05) });

    return {
        background, surface, "surface-hover": hover, field,
        border: color(dark ? 0.40 : 0.80),
        text: readable(dark ? 0.955 : 0.21, neutral * 1.2, 12),
        muted: readable(dark ? 0.71 : 0.50, neutral, 4.5),
        accent, "accent-contrast": label, "accent-text": accentText, "accent-soft": soft,
        "accent-hover": contrast(accentHover, label) >= 4.5 ? accentHover : accent,
        focus: accentText
    };
}

function ensureContrast(start: Color, grounds: string[], target: number, direction: number): string {
    let color = start;
    for (let step = 0; step < 100; step++) {
        const hex = toHex(color);
        if (grounds.every(ground => contrast(hex, ground) >= target)) return hex;
        const next = color.l + direction;
        if (next < 0 || next > 1) return hex;
        color = { ...color, l: next };
    }
    return toHex(color);
}

// Standard sRGB / OKLCH conversion.
const linear = (value: number) => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
const rgb = (hex: string): Rgb => [1, 3, 5].map(offset => parseInt(hex.slice(offset, offset + 2), 16) / 255) as Rgb;

function fromHex(hex: string): Color {
    const [r, g, b] = rgb(hex).map(linear);
    const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
    const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
    const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
    const a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
    const axis = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
    return { l: 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s, c: Math.hypot(a, axis), h: (Math.atan2(axis, a) * 180 / Math.PI + 360) % 360 };
}

function toRgb(color: Color): Rgb {
    const a = color.c * Math.cos(color.h * Math.PI / 180);
    const b = color.c * Math.sin(color.h * Math.PI / 180);
    const l = (color.l + 0.3963377774 * a + 0.2158037573 * b) ** 3;
    const m = (color.l - 0.1055613458 * a - 0.0638541728 * b) ** 3;
    const s = (color.l - 0.0894841775 * a - 1.2914855480 * b) ** 3;
    return [4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s, -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s, -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s];
}

function toHex(color: Color): string {
    const target = { ...color, l: Math.min(1, Math.max(0, color.l)), c: Math.max(0, color.c) };
    const inGamut = (channels: Rgb) => channels.every(value => value >= -0.000001 && value <= 1.000001);
    let channels = toRgb(target);
    if (!inGamut(channels)) {
        // Preserve hue through gamut mapping.
        let low = 0;
        let high = target.c;
        channels = toRgb({ ...target, c: 0 });
        for (let step = 0; step < 24; step++) {
            const mid = (low + high) / 2;
            const candidate = toRgb({ ...target, c: mid });
            if (inGamut(candidate)) { low = mid; channels = candidate; }
            else high = mid;
        }
    }
    return "#" + channels.map(channel => {
        const value = Math.min(1, Math.max(0, channel));
        const encoded = value <= 0.0031308 ? value * 12.92 : 1.055 * value ** (1 / 2.4) - 0.055;
        return Math.round(encoded * 255).toString(16).padStart(2, "0");
    }).join("");
}

export function contrast(first: string, second: string): number {
    const luminance = (hex: string) => { const [r, g, b] = rgb(hex).map(linear); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
    const values = [luminance(first), luminance(second)].sort((a, b) => b - a);
    return (values[0] + 0.05) / (values[1] + 0.05);
}

function hue(hex: string): number {
    const [r, g, b] = rgb(hex);
    const max = Math.max(r, g, b);
    const delta = max - Math.min(r, g, b);
    if (delta < 0.000000001) return 0;
    const value = max === r ? (g - b) / delta : max === g ? (b - r) / delta + 2 : (r - g) / delta + 4;
    return (value * 60 + 360) % 360;
}
