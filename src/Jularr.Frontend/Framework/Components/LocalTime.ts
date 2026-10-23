// Let Intl apply the actual viewer's timezone and DST rules, never Docker's local zone.
export type LocalTimeDisplay = "datetime" | "date" | "time";

export function formatLocalTime(iso: string, display: LocalTimeDisplay, locale: string, timeZone?: string): string | null {
    const value = new Date(iso);
    if (!Number.isFinite(value.getTime())) return null;
    const options: Intl.DateTimeFormatOptions = display === "datetime"
        ? { dateStyle: "short", timeStyle: "short" }
        : display === "date" ? { dateStyle: "short" } : { timeStyle: "short" };
    if (timeZone) options.timeZone = timeZone;
    try {
        return new Intl.DateTimeFormat(locale || undefined, options).format(value);
    } catch {
        return null;
    }
}
