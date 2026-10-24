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

export const BROWSER_TIME_ZONE_COOKIE = "jularr_time_zone";

// Cookie values are IANA zone IDs, not offsets: daylight saving time is resolved at the instant.
export function timeZoneCookieHeader(cookies: string, zone: string | undefined, isHttps: boolean): string | null {
    if (!zone || zone.length > 80 || !/^[A-Za-z0-9_+./-]+$/.test(zone)) return null;
    const current = cookies.split(";").map(part => part.trim()).find(part => part.startsWith(BROWSER_TIME_ZONE_COOKIE + "="));
    if (current?.slice(BROWSER_TIME_ZONE_COOKIE.length + 1) === zone) return null;
    return `${BROWSER_TIME_ZONE_COOKIE}=${zone}; Path=/; Max-Age=31536000; SameSite=Lax${isHttps ? "; Secure" : ""}`;
}

export function localTodayIso(date: Date): string {
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

// Release dates are civil dates: use UTC only for day-number arithmetic, never for display conversion.
export function releaseCountdown(releasedOn: string, today: string, template: string, fallback: string): string {
    const valid = /^\d{4}-\d{2}-\d{2}$/;
    if (!valid.test(releasedOn) || !valid.test(today)) return fallback;
    const days = Math.round((Date.parse(releasedOn + "T00:00:00Z") - Date.parse(today + "T00:00:00Z")) / 86_400_000);
    return days >= 1 && days <= 14 ? template.replace("{0}", String(days)) : fallback;
}
