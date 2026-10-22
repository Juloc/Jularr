// The styled dropdown of every select: a button showing the choice and a popup list of the options. The native select stays in the
// form, visually hidden, as the submitted value and as the field without script; this only mirrors it, in both directions.

export interface DropdownOption { value: string; label: string; disabled: boolean }

const Ready = "dropdownReady";
let counter = 0;

// The options of a select as plain values, flattened over option groups.
export function optionsOf(select: HTMLSelectElement): DropdownOption[] {
    return Array.from(select.options).map(option => ({ value: option.value, label: option.textContent?.trim() ?? "", disabled: option.disabled || option.parentElement instanceof HTMLOptGroupElement && option.parentElement.disabled }));
}

// The next enabled option from a position in a direction (wrapping is not wanted: a list ends).
export function step(options: readonly DropdownOption[], from: number, direction: 1 | -1): number {
    for (let index = from + direction; index >= 0 && index < options.length; index += direction) if (!options[index].disabled) return index;
    return from;
}

// The first enabled option whose label starts with the typed text, searching on from the current one.
export function match(options: readonly DropdownOption[], typed: string, from: number): number {
    const text = typed.toLowerCase();
    const order = [...options.keys()].map(index => (index + (typed.length === 1 ? from + 1 : from)) % options.length);
    return order.find(index => !options[index].disabled && options[index].label.toLowerCase().startsWith(text)) ?? -1;
}

// The fraction of the page's free space decides whether the list opens below the button or above it.
export function placement(rect: { top: number; bottom: number; left: number; width: number }, viewportHeight: number, viewportWidth: number, listHeight: number):
    { top: number; left: number; width: number; maxHeight: number; above: boolean } {
    const gap = 6, margin = 10;
    const below = viewportHeight - rect.bottom - gap - margin, above = rect.top - gap - margin;
    const opensAbove = listHeight > below && above > below;
    const maxHeight = Math.max(120, opensAbove ? above : below);
    const height = Math.min(listHeight, maxHeight);
    const width = Math.min(Math.max(rect.width, 160), viewportWidth - 2 * margin);
    return {
        top: opensAbove ? rect.top - gap - height : rect.bottom + gap,
        left: Math.min(Math.max(rect.left, margin), viewportWidth - width - margin),
        width, maxHeight, above: opensAbove
    };
}

export function enhanceSelect(select: HTMLSelectElement): void {
    if (select.dataset[Ready] || select.multiple || select.size > 1 || select.hasAttribute("data-native")) return;
    select.dataset[Ready] = "true";
    const id = select.id || `dropdown-${++counter}`;
    const button = document.createElement("button");
    button.type = "button";
    button.className = `${select.className} ui-dropdown__button`.trim();
    button.id = `${id}-button`;
    button.setAttribute("aria-haspopup", "listbox");
    button.setAttribute("aria-expanded", "false");
    const value = document.createElement("span");
    value.className = "ui-dropdown__value";
    button.append(value);
    const popup = document.createElement("div");
    popup.className = "ui-dropdown__popup";
    popup.id = `${id}-list`;
    popup.setAttribute("popover", "auto");
    button.popoverTargetElement = popup;
    popup.setAttribute("role", "listbox");
    popup.tabIndex = -1;
    button.setAttribute("aria-controls", popup.id);
    // The select is the field the labels name; the button and the list take their names from it.
    const labels = Array.from(select.labels ?? []);
    const names = labels.map((label, index) => label.id || (label.id = `${id}-label-${index}`));
    if (names.length) button.setAttribute("aria-labelledby", `${names.join(" ")} ${value.id = `${id}-value`}`);
    else if (select.getAttribute("aria-label")) button.setAttribute("aria-label", select.getAttribute("aria-label")!);
    popup.setAttribute("aria-label", select.getAttribute("aria-label") ?? labels.map(label => label.textContent?.trim()).join(" "));
    select.classList.add("ui-dropdown__native");
    select.tabIndex = -1;
    select.setAttribute("aria-hidden", "true");
    select.after(button, popup);

    let options: DropdownOption[] = [];
    let active = -1;
    let typed = "";
    let typedAt = 0;

    const rows = () => Array.from(popup.querySelectorAll<HTMLElement>(".ui-dropdown__option"));

    function build(): void {
        options = optionsOf(select);
        popup.replaceChildren(...options.map((option, index) => {
            const row = document.createElement("div");
            row.className = "ui-dropdown__option";
            row.id = `${popup.id}-${index}`;
            row.setAttribute("role", "option");
            row.dataset.index = String(index);
            row.tabIndex = -1;
            if (option.disabled) row.setAttribute("aria-disabled", "true");
            const label = document.createElement("span");
            label.textContent = option.label;
            row.append(label);
            return row;
        }));
        sync();
    }

    function sync(): void {
        const index = select.selectedIndex;
        value.textContent = options[index]?.label ?? "";
        value.classList.toggle("ui-dropdown__value--empty", options[index]?.value === "");
        button.disabled = select.disabled;
        button.hidden = select.hidden;
        button.toggleAttribute("inert", select.hasAttribute("inert"));
        rows().forEach((row, position) => row.setAttribute("aria-selected", String(position === index)));
    }

    function focusRow(index: number): void {
        active = index;
        const list = rows();
        list.forEach((row, position) => row.classList.toggle("ui-dropdown__option--active", position === index));
        const row = list[index];
        if (row) {
            popup.setAttribute("aria-activedescendant", row.id);
            row.scrollIntoView({ block: "nearest" });
        }
    }

    function choose(index: number): void {
        if (index < 0 || options[index]?.disabled) return;
        const changed = select.selectedIndex !== index;
        select.selectedIndex = index;
        sync();
        popup.hidePopover();
        button.focus();
        if (changed) {
            select.dispatchEvent(new Event("input", { bubbles: true }));
            select.dispatchEvent(new Event("change", { bubbles: true }));
        }
    }

    function position(): void {
        // In a field the list lines up with the field, not with the button inside its padding.
        const rect = (button.closest(".ui-field__control") ?? button).getBoundingClientRect();
        const placed = placement(rect, window.innerHeight, window.innerWidth, popup.scrollHeight);
        popup.style.setProperty("--dropdown-top", `${placed.top}px`);
        popup.style.setProperty("--dropdown-left", `${placed.left}px`);
        popup.style.setProperty("--dropdown-width", `${placed.width}px`);
        popup.style.setProperty("--dropdown-max-height", `${placed.maxHeight}px`);
        popup.classList.toggle("ui-dropdown__popup--above", placed.above);
    }

    popup.addEventListener("beforetoggle", event => {
        if ((event as ToggleEvent).newState === "open") { sync(); position(); }
    });
    popup.addEventListener("toggle", event => {
        const open = (event as ToggleEvent).newState === "open";
        button.setAttribute("aria-expanded", String(open));
        if (open) {
            position();
            popup.focus({ preventScroll: true });
            focusRow(Math.max(select.selectedIndex, 0));
        } else {
            popup.removeAttribute("aria-activedescendant");
        }
    });
    button.addEventListener("keydown", event => {
        if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            if (!button.disabled && !popup.matches(":popover-open")) popup.showPopover();
        }
    });
    popup.addEventListener("click", event => {
        const row = (event.target as Element).closest<HTMLElement>(".ui-dropdown__option");
        if (row) choose(Number(row.dataset.index));
    });
    popup.addEventListener("pointermove", event => {
        const row = (event.target as Element).closest<HTMLElement>(".ui-dropdown__option");
        if (row && !row.hasAttribute("aria-disabled") && Number(row.dataset.index) !== active) focusRow(Number(row.dataset.index));
    });
    popup.addEventListener("keydown", event => {
        const last = options.length - 1;
        switch (event.key) {
            case "ArrowDown": event.preventDefault(); focusRow(step(options, active, 1)); return;
            case "ArrowUp": event.preventDefault(); focusRow(step(options, active, -1)); return;
            case "Home": event.preventDefault(); focusRow(step(options, -1, 1)); return;
            case "End": event.preventDefault(); focusRow(step(options, last + 1, -1)); return;
            case "Enter": case " ": event.preventDefault(); choose(active); return;
            case "Tab": popup.hidePopover(); return;
        }
        if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
            const now = Date.now();
            typed = now - typedAt > 700 ? event.key : typed + event.key;
            typedAt = now;
            const found = match(options, typed, active);
            if (found >= 0) focusRow(found);
        }
    });
    // A click on the field's label or a script changing the select shows in the button.
    select.addEventListener("change", sync);
    select.addEventListener("focus", () => button.focus());
    new MutationObserver(build).observe(select, { childList: true, subtree: true, characterData: true });
    new MutationObserver(sync).observe(select, { attributes: true, attributeFilter: ["disabled", "hidden", "inert"] });
    // A reset of the form restores the select; the button follows once the browser has done it.
    select.form?.addEventListener("reset", () => queueMicrotask(sync));
    build();
}

// An open list closes where it would drift from its button: on resize and on scrolling anything but itself.
export function installDropdowns(): void {
    const openLists = () => Array.from(document.querySelectorAll<HTMLElement>(".ui-dropdown__popup:popover-open"));
    window.addEventListener("resize", () => openLists().forEach(list => list.hidePopover()));
    document.addEventListener("scroll", event => openLists().filter(list => !list.contains(event.target as Node)).forEach(list => list.hidePopover()), true);
}

export function enhanceDropdowns(root: ParentNode = document): void {
    if (typeof HTMLElement.prototype.showPopover !== "function") return;
    root.querySelectorAll<HTMLSelectElement>("select:not([multiple]):not([data-native])").forEach(enhanceSelect);
}
