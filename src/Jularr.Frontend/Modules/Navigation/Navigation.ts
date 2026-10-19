// Which links and forms swap the app content in place; everything else (other shells, files, new windows) loads normally.
// data-navigation="full" also marks links a page script handles itself (a preview dialog, the next results).

const FullPagePrefixes = ["/Player/", "/Read/", "/Game/", "/Account/", "/Setup", "/api/"];

export interface NavigationElement {
    readonly target: string;
    readonly dataset: DOMStringMap;
    hasAttribute(name: string): boolean;
}

export function staysInApp(element: NavigationElement, address: string, current: Location): boolean {
    if (element.target && element.target !== "_self" || element.hasAttribute("download") || element.dataset.navigation === "full") return false;
    const url = new URL(address || current.href, current.href);
    if (url.origin !== current.origin) return false;
    return !FullPagePrefixes.some(prefix => url.pathname.startsWith(prefix));
}
