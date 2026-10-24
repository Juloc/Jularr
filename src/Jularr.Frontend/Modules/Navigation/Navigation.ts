// Which links and forms swap the app content in place; everything else (other shells, files, new windows) loads normally.
// data-navigation="full" also marks links a page script handles itself (a preview dialog, the next results).

const FullPagePrefixes = ["/Player/", "/Read/", "/Game/", "/Account/", "/Setup", "/api/"];

export interface NavigationElement {
    readonly target: string;
    readonly dataset: DOMStringMap;
    hasAttribute(name: string): boolean;
}

// An in-place response can come from a newer deployment while this tab still has the old shell CSS/JS.
export function hasNewBuild(currentBuild: string | undefined, responseHtml: string | undefined): boolean {
    if (!currentBuild || !responseHtml) return false;
    const build = /<html\b[^>]*\bdata-build="([^"]+)"/.exec(responseHtml)?.[1];
    return build !== undefined && build !== currentBuild;
}

export function staysInApp(element: NavigationElement, address: string, current: Location): boolean {
    if (element.target && element.target !== "_self" || element.hasAttribute("download") || element.dataset.navigation === "full") return false;
    const url = new URL(address || current.href, current.href);
    if (url.origin !== current.origin) return false;
    return !FullPagePrefixes.some(prefix => url.pathname.startsWith(prefix));
}
