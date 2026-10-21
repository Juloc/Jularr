interface Color { l: number; c: number; h: number }
type Rgb = [number, number, number];
type Tokens = Record<string, string>;

export const OriginalAccent = "#c8102e";
export const CleanAccent = "#813eff";

// The reference palette of the owner's mockups (20 Oct), used verbatim for the default accent; muted text and accent text are
// the mockups' tones moved until they pass AA (4.5:1) on every surface. Any other accent derives its palette in mode().
const Reference: { light: Tokens; dark: Tokens } = {
    light: {
        background: "#faf9ff", surface: "#ffffff", "surface-hover": "#f5f2ff", field: "#f8f7ff", border: "#ebe8f5",
        text: "#242039", muted: "#6c6882", accent: "#813eff", "accent-contrast": "#ffffff", "accent-text": "#6e34d2",
        "accent-soft": "#f0eaff", "accent-hover": "#7427ed", focus: "#6e34d2"
    },
    dark: {
        background: "#11111a", surface: "#1c1b29", "surface-hover": "#2b2640", field: "#252233", border: "#363247",
        text: "#f4f1fd", muted: "#aaa3bf", accent: "#a980ff", "accent-contrast": "#11101a", "accent-text": "#baa0ff",
        "accent-soft": "#33284e", "accent-hover": "#b794ff", focus: "#baa0ff"
    }
};

// One seed, two accessible palettes.
export function buildPalette(seed: string): { light: Tokens; dark: Tokens; rotation: string; saturation: string } {
    if (!/^#[a-f\d]{6}$/i.test(seed)) throw new RangeError("Expected a six-digit hex accent.");
    const color = fromHex(seed);
    const monochrome = color.c < 0.025;
    const rotation = (monochrome ? 0 : ((hue(seed) - hue(OriginalAccent)) % 360 + 540) % 360 - 180).toFixed(1) + "deg";
    if (seed.toLowerCase() === CleanAccent) return { light: Reference.light, dark: Reference.dark, rotation, saturation: "1" };
    return { light: mode(color, monochrome, false), dark: mode(color, monochrome, true), rotation, saturation: monochrome ? "0" : "1" };
}

function mode(seed: Color, monochrome: boolean, dark: boolean): Tokens {
    // Owner's decision 20 Oct: the tokens of the owner's mockups. Neutrals are very light (or deep) tints of the accent hue,
    // lines are the quiet 1px colour, the page is white with a lavender breath. A monochrome seed has no tint at all.
    const chroma = monochrome ? 0 : Math.min(0.3, Math.max(0.09, seed.c));
    const tint = monochrome ? 0 : Math.min(1, seed.c / 0.2);
    const color = (l: number, c: number) => toHex({ l, c: c * tint, h: seed.h });
    const background = color(dark ? 0.182 : 0.985, dark ? 0.018 : 0.008);
    const surface = color(dark ? 0.230 : 1, dark ? 0.027 : 0);
    const field = color(dark ? 0.263 : 0.979, dark ? 0.032 : 0.011);
    const hover = color(dark ? 0.285 : 0.968, dark ? 0.045 : 0.018);
    const soft = color(dark ? 0.309 : 0.948, dark ? 0.067 : 0.029);
    const grounds = [background, surface, hover, field, soft];
    const direction = dark ? 0.01 : -0.01;
    const readable = (l: number, c: number, target: number, backgrounds = grounds) => ensureContrast({ l, c: c * tint, h: seed.h }, backgrounds, target, direction);

    // Keep actions and labels readable: the dark accent is the lighter, calmer tone of the same colour.
    let solid: Color = dark
        ? { l: monochrome ? 0.93 : Math.min(0.80, Math.max(0.62, seed.l + 0.13)), c: chroma * 0.7, h: seed.h }
        : { l: monochrome ? 0.22 : Math.min(0.82, Math.max(0.40, seed.l)), c: chroma, h: seed.h };
    const ink = toHex({ l: 0.18, c: Math.min(chroma * 0.2, 0.02), h: seed.h });
    let accent = toHex(solid);
    let label = "#ffffff";
    for (let step = 0; step < 60; step++) {
        accent = toHex(solid);
        if (contrast(accent, "#ffffff") >= 4.5) break;
        if (contrast(accent, ink) >= 4.5) { label = ink; break; }
        solid = { ...solid, l: solid.l + (solid.l >= 0.62 ? 0.01 : -0.01) };
    }
    const accentText = ensureContrast(
        { l: monochrome ? (dark ? 0.90 : 0.25) : (dark ? Math.max(solid.l, 0.765) : Math.min(solid.l, 0.50)), c: chroma * (dark ? 0.55 : 0.85), h: seed.h },
        [...grounds], 4.5, direction);
    const accentHover = toHex({ ...solid, l: solid.l + (label === "#ffffff" ? -0.05 : 0.05) });

    return {
        background, surface, "surface-hover": hover, field,
        border: color(dark ? 0.330 : 0.937, dark ? 0.037 : 0.018),
        text: readable(dark ? 0.964 : 0.261, dark ? 0.016 : 0.046, 12),
        muted: readable(dark ? 0.731 : 0.56, dark ? 0.041 : 0.04, 4.5),
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
