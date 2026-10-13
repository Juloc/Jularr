// EmulatorJS ships these languages; others keep English.
const Languages: Record<string, string> = {
    af: "af-FR", ar: "ar-AR", bn: "ben-BEN", de: "de-GER", el: "el-GR", es: "es-ES", fa: "fa-AF", hi: "hi-HI", it: "it-IT", ja: "ja-JA",
    jv: "jv-JV", ko: "ko-KO", pt: "pt-BR", ro: "ro-RO", ru: "ru-RU", tr: "tr-TR", vi: "vi-VN", zh: "zh-CN"
};

export function emulatorLanguage(locale: string): string {
    return Languages[locale.split("-")[0].toLowerCase()] ?? "en-US";
}

export function toBase64(bytes: Uint8Array): string {
    let text = "";
    for (let index = 0; index < bytes.length; index += 0x8000) text += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
    return btoa(text);
}

export function fromBase64(text: string): Uint8Array {
    const binary = atob(text);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    return bytes;
}

export function sameBytes(left: Uint8Array | undefined, right: Uint8Array): boolean {
    return !!left && left.length === right.length && left.every((value, index) => value === right[index]);
}
