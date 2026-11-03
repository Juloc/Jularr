// A device choice that changes how a page looks or is laid out (skin, brightness, the layout of lists) is also a cookie under the same name,
// so the server writes it into the page (DevicePreferences.cs) and nothing changes after the first paint. The browser storage stays its source.
export function rememberDevicePreference(name: string, value: string): void {
    try {
        localStorage.setItem(name, value);
    } catch {
        // Storage is optional; the cookie still carries the choice.
    }
    shareDevicePreference(name, value);
}

// Writes the cookie only when it differs, so a choice stored before the cookie existed reaches the server from the next page on.
export function shareDevicePreference(name: string, value: string): void {
    const current = document.cookie.split(";").map(part => part.trim()).find(part => part.startsWith(name + "="));
    if (current?.slice(name.length + 1) === value) return;
    document.cookie = `${name}=${value}; Path=/; Max-Age=31536000; SameSite=Lax${location.protocol === "https:" ? "; Secure" : ""}`;
}
