interface Color { l: number; c: number; h: number }
type Rgb = [number, number, number];
type Tokens = Record<string, string>;

export const OriginalAccent = "#c8102e";
export const CleanAccent = "#8040f7";

// The owner's mockup palette (jobs redesign, 21 Oct), verbatim: it is the palette of the default accent. The names are the app's tokens:
// background = bg, surface = panel, field/surface-hover = panel2, accent-soft = lav, accent = purple, accent-outline/focus = purple-soft,
// accent-text = ptext, text = ink, border = line, border-strong = line2, accent-edge = the active chip's #dcd0ff outline.
const Reference: { light: Tokens; dark: Tokens } = {
    light: {
        background: "#faf9ff", surface: "#ffffff", field: "#f8f7ff", "surface-hover": "#f8f7ff", "accent-soft": "#f2edff",
        accent: "#8040f7", "accent-outline": "#b59aff", focus: "#b59aff", "accent-text": "#7136d9", "accent-edge": "#dcd0ff",
        text: "#26223d", muted: "#938fa9", border: "#eae6f4", "border-strong": "#e1dcee", "accent-contrast": "#ffffff"
    },
    dark: {
        background: "#12121d", surface: "#1d1b2b", field: "#262339", "surface-hover": "#262339", "accent-soft": "#35294e",
        accent: "#9a70ff", "accent-outline": "#8060bf", focus: "#8060bf", "accent-text": "#c3a8ff", "accent-edge": "#dcd0ff",
        text: "#f4f1ff", muted: "#aba5bf", border: "#38334e", "border-strong": "#4b4260", "accent-contrast": "#ffffff"
    }
};

// One seed, two palettes: the reference palette with its hue turned to the seed's and its tints scaled to the seed's chroma, so the
// default accent reproduces the mockup exactly and any other accent gets the same hue-tinted neutrals, lavender and lines.
export function buildPalette(seed: string): { light: Tokens; dark: Tokens; rotation: string; saturation: string } {
    if (!/^#[a-f\d]{6}$/i.test(seed)) throw new RangeError("Expected a six-digit hex accent.");
    const color = fromHex(seed);
    const monochrome = color.c < 0.025;
    const rotation = (monochrome ? 0 : ((hue(seed) - hue(OriginalAccent)) % 360 + 540) % 360 - 180).toFixed(1) + "deg";
    if (seed.toLowerCase() === CleanAccent) return { light: Reference.light, dark: Reference.dark, rotation, saturation: "1" };
    return { light: mode(color, seed, monochrome, false), dark: mode(color, seed, monochrome, true), rotation, saturation: monochrome ? "0" : "1" };
}

function mode(seed: Color, seedHex: string, monochrome: boolean, dark: boolean): Tokens {
    const referenceSeed = fromHex(CleanAccent);
    const reference = dark ? Reference.dark : Reference.light;
    // Tints follow the seed's chroma, never fainter than three fifths of the mockup's (none for a monochrome seed); the hue turns by the seed's distance from the reference hue.
    const scale = monochrome ? 0 : Math.min(1.25, Math.max(0.6, seed.c / referenceSeed.c));
    const turn = seed.h - referenceSeed.h;
    const tinted = (name: string): string => {
        const base = fromHex(reference[name]);
        return toHex({ l: base.l, c: base.c * scale, h: base.h + turn });
    };

    // The accent is the seed itself in light and its lighter sibling in dark; its text colour keeps the mockup's step from it.
    const accent = dark
        ? { l: monochrome ? 0.93 : Math.min(0.8, Math.max(0.62, seed.l + 0.095)), c: seed.c * 0.81, h: seed.h + 1.3 }
        : { l: monochrome ? 0.22 : seed.l, c: seed.c, h: seed.h };
    const accentText = dark
        ? { l: monochrome ? 0.9 : Math.min(0.85, Math.max(0.74, seed.l + 0.227)), c: seed.c * 0.49, h: seed.h + 5.6 }
        : { l: monochrome ? 0.25 : Math.min(0.56, seed.l - 0.052), c: seed.c * 0.91, h: seed.h + 0.6 };
    const accentHex = dark || monochrome ? toHex(accent) : seedHex.toLowerCase();

    return {
        background: tinted("background"), surface: tinted("surface"), field: tinted("field"), "surface-hover": tinted("surface-hover"),
        "accent-soft": tinted("accent-soft"), accent: accentHex, "accent-outline": tinted("accent-outline"), focus: tinted("focus"),
        "accent-text": toHex(accentText), "accent-edge": tinted("accent-edge"),
        text: tinted("text"), muted: tinted("muted"), border: tinted("border"), "border-strong": tinted("border-strong"),
        // White on the accent, unless the accent itself is a light colour.
        "accent-contrast": accent.l > 0.78 ? toHex({ l: 0.2, c: Math.min(seed.c * 0.2, 0.02), h: seed.h }) : "#ffffff"
    };
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
