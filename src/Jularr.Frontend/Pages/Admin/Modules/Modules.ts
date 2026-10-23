// The rules of the module switches, as the Backend enforces them when it saves: a module can be on only while every module it needs is on,
// and a module that others need stays on while they are. The page shows the same rules while a person changes the switches.

export interface ModuleSwitch {
    key: string;
    requires: string[];
    on: boolean;
}

export interface ModuleView {
    // The modules this one needs that are off; while there are any it cannot be switched on.
    missing: string[];
    // The modules that need this one and are on; while there are any it cannot be switched off.
    dependants: string[];
    blocked: boolean;
}

export function viewOf(modules: ModuleSwitch[], key: string): ModuleView {
    const module = modules.find(item => item.key === key);
    if (!module) return { missing: [], dependants: [], blocked: false };
    const missing = module.requires.filter(required => !modules.some(item => item.key === required && item.on));
    const dependants = modules.filter(item => item.on && item.requires.includes(key)).map(item => item.key);
    return { missing, dependants, blocked: module.on ? dependants.length > 0 : missing.length > 0 };
}

// Reads the switches of the page's cards.
export function switchesOf(root: ParentNode): ModuleSwitch[] {
    return Array.from(root.querySelectorAll<HTMLElement>("[data-module]")).map(card => ({
        key: card.dataset.module ?? "",
        requires: (card.dataset.requires ?? "").split(" ").filter(Boolean),
        on: card.querySelector<HTMLInputElement>("input[type=checkbox]")?.checked === true
    }));
}

// The names that fill the line "Needed by ...", in the order the page lists the modules.
export function dependantNames(names: Record<string, string>, dependants: string[]): string {
    return dependants.map(key => names[key] ?? key).join(", ");
}

// The modules that go on with this one: what it needs and what that needs, in the order they are found; the module itself comes first.
export function withRequired(modules: ModuleSwitch[], key: string): string[] {
    const found: string[] = [];
    const visit = (current: string): void => {
        if (found.includes(current)) return;
        found.push(current);
        for (const required of modules.find(item => item.key === current)?.requires ?? []) visit(required);
    };
    visit(key);
    return found;
}
