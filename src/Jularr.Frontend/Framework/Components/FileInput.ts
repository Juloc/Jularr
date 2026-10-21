// The file input: a drop zone with a button, the chosen file with its size and a remove button. The native file field stays behind it
// (it opens the system dialog and is the submitted value; without script it is the field). The rules live here, free of the page.

export interface FileRule {
    accept: string;
    maximumBytes: number;
}

export interface FileInfo {
    name: string;
    size: number;
}

export type FileProblem = "empty" | "size" | "type";

export interface FileMessages {
    empty: string;
    size: string;
    type: string;
}

// What the controller needs from the page: the native field and the places that show the state.
export interface FileParts {
    native: { files: ArrayLike<FileInfo> | null; clear(): void; open(): void; assign(file: FileInfo): void; notify(): void };
    show(file: FileInfo | null, size: string): void;
    error(message: string | null): void;
    dragging(active: boolean): void;
}

// ".json, .CBZ" and a bare "image/*" are written as the lower-case entries the field accepts.
export function acceptEntries(accept: string): string[] {
    return accept.split(",").map(entry => entry.trim().toLowerCase()).filter(entry => entry.length > 0);
}

export function fileProblem(file: FileInfo & { type?: string }, rule: FileRule): FileProblem | null {
    if (file.size <= 0) return "empty";
    if (rule.maximumBytes > 0 && file.size > rule.maximumBytes) return "size";
    const entries = acceptEntries(rule.accept);
    if (entries.length === 0) return null;
    const name = file.name.toLowerCase();
    const type = (file.type ?? "").toLowerCase();
    const fits = entries.some(entry => entry.startsWith(".") ? name.endsWith(entry) : entry.endsWith("/*") ? type.startsWith(entry.slice(0, -1)) : type === entry);
    return fits ? null : "type";
}

// Decimal units, as the server writes sizes (ByteSize.cs): "12 B", "4.2 KB", "1.0 MB".
export function formatSize(bytes: number, locale?: string): string {
    const units = ["B", "KB", "MB", "GB", "TB", "PB"];
    let value = bytes;
    let unit = 0;
    while (value >= 1000 && unit < units.length - 1) {
        value /= 1000;
        unit++;
    }

    const digits = unit === 0 ? 0 : 1;
    return value.toLocaleString(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits }) + " " + units[unit];
}

// The first of several dropped files; a file field takes one.
export function firstFile<T>(files: ArrayLike<T> | null | undefined): T | null {
    return files && files.length > 0 ? files[0] : null;
}

// The controller of one file input. The field's own change event comes from the system dialog; what the controller itself changes
// (a drop, a removal, a refused file) it announces once with `notify`, which its own listener ignores.
export function attach(parts: FileParts, rule: FileRule, messages: FileMessages, locale?: string) {
    let announcing = false;
    const announce = () => {
        announcing = true;
        try { parts.native.notify(); } finally { announcing = false; }
    };

    // A file that does not fit is taken out of the field again, with the reason.
    const refuse = (problem: FileProblem) => {
        parts.native.clear();
        announce();
        parts.show(null, "");
        parts.error(messages[problem]);
    };

    const show = (file: FileInfo) => {
        parts.error(null);
        parts.show(file, formatSize(file.size, locale));
    };

    return {
        // The field changed through the system dialog.
        changed() {
            if (announcing) return;
            const file = firstFile(parts.native.files);
            const problem = file ? fileProblem(file, rule) : null;
            if (file && problem) refuse(problem);
            else if (file) show(file);
            else {
                parts.error(null);
                parts.show(null, "");
            }
        },
        choose: () => parts.native.open(),

        // A drop replaces the file; the field is the one value, so it receives the dropped file too.
        drop(files: ArrayLike<FileInfo> | null | undefined) {
            parts.dragging(false);
            const file = firstFile(files);
            if (!file) return;
            const problem = fileProblem(file, rule);
            if (problem) {
                refuse(problem);
                return;
            }

            parts.native.assign(file);
            announce();
            show(file);
        },
        dragging: (active: boolean) => parts.dragging(active),
        remove() {
            parts.native.clear();
            announce();
            parts.error(null);
            parts.show(null, "");
        }
    };
}
