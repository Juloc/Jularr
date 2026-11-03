// The address of a GET form with its current values; empty values are left out, so an empty search leaves no query behind.
export function formAddress(action: string, values: Pick<FormData, "forEach">, base: string): string {
    const address = new URL(action, base);
    address.search = "";
    values.forEach((value, name) => {
        if (typeof value === "string" && value.trim()) address.searchParams.append(name, value.trim());
    });
    return address.pathname + lists(address.search);
}

// The same address with this search, or without one when it is empty; true when that changed it.
export function withQuery(href: string, query: string, base: string): { href: string; changed: boolean } {
    const address = new URL(href, base);
    const before = address.searchParams.get("q") ?? "";
    const wanted = query.trim();
    if (before === wanted) return { href, changed: false };
    if (wanted) address.searchParams.set("q", wanted);
    else address.searchParams.delete("q");
    return { href: address.pathname + lists(address.search) + address.hash, changed: true };
}

// Lists of values (type=movies,series) keep their commas, as the server writes them.
const lists = (search: string) => search.replace(/%2C/gi, ",");
