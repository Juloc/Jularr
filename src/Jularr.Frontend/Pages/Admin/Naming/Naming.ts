// A placeholder is written into a field where the caret is (or over the selected text), and the caret follows it.
export function insertToken(value: string, start: number, end: number, token: string): { value: string; caret: number } {
    const from = Math.max(0, Math.min(start, value.length));
    const to = Math.max(from, Math.min(end, value.length));
    return { value: value.slice(0, from) + token + value.slice(to), caret: from + token.length };
}

// The address that asks the Backend what a typed pattern would make of a sample title.
export function previewAddress(mediaType: string, folder: string, file: string): string {
    const query = new URLSearchParams({ mediaTypeId: mediaType, file });
    if (folder.trim() !== "") query.set("folder", folder);
    return "/api/v1/admin/library/naming/preview?" + query.toString();
}
