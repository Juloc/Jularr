// Development-only scope examples. No network or persistent storage.
export function initializeSettings(root: HTMLElement, player: HTMLElement, panel: HTMLElement): void {
    const fields = Array.from(root.querySelectorAll<HTMLInputElement | HTMLSelectElement>("[data-preview-setting]"));
    const defaults = new Map(fields.map(field => [field.name, fieldValue(field)]));
    defaults.set("fit", "contain");
    const scopes = new Map(["profile", "type", "series", "current", "admin"].map(name => [name, new Map<string, string>()]));
    const order = ["current", "series", "type", "profile"];
    const corrections = new Set(["audioOffset", "primaryOffset", "secondaryOffset", "primaryRate", "secondaryRate"]);
    const captions = root.querySelector<HTMLElement>(".player-workshop__captions")!;
    const subtitle = player.querySelector<HTMLElement>('[data-action="subtitle-toggle"]')!;
    const full = root.querySelector<HTMLElement>("[data-settings-page]")!;
    const stage = root.querySelector<HTMLElement>(".player-workshop__stage")!;
    const stageHome = stage.parentElement!;
    const chapters = root.querySelector<HTMLElement>(".player-workshop__chapter-section")!;
    const video = player.querySelector<HTMLVideoElement>("video")!;
    const inheritedLabel = root.querySelector("[data-setting-origin]")!.textContent;
    const overriddenLabel = root.querySelector('[data-setting-message="overridden"]')!.textContent;
    let scope = "current";

    function inherited(name: string, from = scope): string {
        const candidates = from === "admin" ? ["admin"] : order.slice(order.indexOf(from));
        for (const candidate of candidates) {
            const value = scopes.get(candidate)!.get(name);
            if (value !== undefined) return value;
        }
        return defaults.get(name)!;
    }

    function render(): void {
        fields.forEach(field => {
            const value = inherited(field.name);
            if (field.type === "checkbox") (field as HTMLInputElement).checked = value === "true";
            else field.value = value;
            field.disabled = scope === "admin" && !corrections.has(field.name);
            if (field.name === "sleepMinutes") field.closest<HTMLElement>("[data-setting-row]")!.hidden = inherited("sleep") !== "custom";
        });
        root.querySelectorAll<HTMLElement>("[data-reset-setting]").forEach(button => {
            button.hidden = !scopes.get(scope)!.has(button.dataset.resetSetting!);
        });
        root.querySelectorAll<HTMLElement>("[data-setting-origin]").forEach(label => {
            label.textContent = scopes.get(scope)!.has(label.dataset.settingOrigin!) ? overriddenLabel : inheritedLabel;
        });
        root.querySelectorAll<HTMLElement>("[data-range-value]").forEach(label => label.textContent = inherited(label.dataset.rangeValue!));
        root.querySelectorAll<HTMLButtonElement>("[data-speed], [data-fit]").forEach(button => {
            const selected = button.dataset.speed ? inherited("speed") === button.dataset.speed : inherited("fit") === button.dataset.fit;
            button.setAttribute("aria-pressed", String(selected));
            button.disabled = scope === "admin";
            if (button.dataset.speed) button.style.left = `${(Number(button.dataset.speed) - .25) / 3.75 * 100}%`;
        });
        root.querySelectorAll<HTMLButtonElement>("[data-copy]").forEach(button => button.disabled = scope === "admin");
        root.querySelectorAll<HTMLButtonElement>("[data-offset]").forEach(button => button.disabled = scope === "admin" && !corrections.has(button.dataset.offset!));

        // Same preview, both subtitle tracks.
        captions.hidden = subtitle.getAttribute("aria-pressed") !== "true";
        captions.style.bottom = `max(88px, ${inherited("bottomOffset")}%)`;
        captions.style.maxWidth = `${inherited("maxWidth")}%`;
        captions.style.fontFamily = inherited("font") === "system" ? "inherit" : inherited("font");
        captions.style.setProperty("--caption-size", `${inherited("fontSize")}px`);
        captions.style.setProperty("--caption-color", inherited("textColor"));
        captions.style.setProperty("--caption-background", String(Number(inherited("backgroundOpacity")) / 100));
        captions.style.setProperty("--caption-opacity", String(Number(inherited("textOpacity")) / 100));
        captions.style.setProperty("--caption-outline", `${inherited("outline")}px`);
        captions.style.setProperty("--caption-shadow", inherited("shadow") === "true" ? "0 2px 4px #000" : "none");
        captions.querySelector<HTMLElement>('[data-caption="primary"]')!.hidden = inherited("primarySubtitle") === "";
        const secondary = captions.querySelector<HTMLElement>('[data-caption="secondary"]')!;
        secondary.hidden = inherited("secondarySubtitle") === "";
        secondary.style.fontSize = inherited("separateStyle") === "true" ? `${inherited("secondaryFontSize")}px` : "inherit";
        secondary.style.color = inherited("separateStyle") === "true" ? inherited("secondaryColor") : "inherit";
        video.style.objectFit = inherited("fit");
        video.style.transform = inherited("zoom") === "100" ? "none" : `scale(${Number(inherited("zoom")) / 100})`;
        video.style.filter = [["brightness", "brightness"], ["contrast", "contrast"], ["saturate", "saturation"]]
            .filter(([, name]) => inherited(name) !== "100").map(([effect, name]) => `${effect}(${inherited(name)}%)`).join(" ") || "none";
    }

    function change(name: string, value: string): void {
        if (scope === "admin" && !corrections.has(name)) return;
        scopes.get(scope)!.set(name, value);
        if (name === "subtitlePolicy") subtitle.setAttribute("aria-pressed", String(value !== "off"));
        render();
    }
    fields.forEach(field => field.addEventListener("input", () => {
        if (field instanceof HTMLInputElement && !field.checkValidity()) return;
        change(field.name, fieldValue(field));
    }));
    root.querySelectorAll<HTMLSelectElement>("[data-setting-scope]").forEach(select => select.addEventListener("change", () => {
        scope = select.value;
        root.querySelectorAll<HTMLSelectElement>("[data-setting-scope]").forEach(item => item.value = scope);
        render();
    }));

    // SSR panes, native disclosures.
    root.querySelectorAll<HTMLElement>("[data-settings-tabs]").forEach(tabs => tabs.addEventListener("click", event => {
        const link = (event.target as Element).closest<HTMLAnchorElement>("a");
        if (!link) return;
        event.preventDefault();
        tabs.querySelectorAll("a").forEach(item => {
            item.classList.toggle("navigation-tabs__link--active", item === link);
            if (item === link) item.setAttribute("aria-current", "page");
            else item.removeAttribute("aria-current");
            root.querySelector<HTMLElement>(item.hash)!.hidden = item !== link;
        });
        positionPopover(panel);
    }));

    function showFull(): void {
        const expanded = location.hash === "#player-settings-page";
        if (panel.matches(":popover-open")) panel.hidePopover();
        root.classList.toggle("player-workshop--settings", expanded);
        root.classList.remove("player-workshop--mini");
        player.classList.remove("player--mini");
        full.hidden = !expanded;
        stageHome.hidden = expanded;
        chapters.hidden = expanded;
        if (expanded) full.querySelector("[data-settings-preview]")!.append(stage);
        else stageHome.prepend(stage);
        root.querySelectorAll<HTMLElement>("[data-sample-pane]").forEach(item => item.hidden = expanded);
        render();
    }
    window.addEventListener("hashchange", showFull);

    function positionPopover(popover: HTMLElement): void {
        if (!popover.matches(":popover-open")) return;
        const anchor = root.querySelector<HTMLElement>(`[popovertarget="${popover.id}"]`)!.getBoundingClientRect();
        const bounds = popover.getBoundingClientRect();
        const fullscreen = popover === panel && (innerWidth < 620 || innerHeight < 650);
        popover.classList.toggle("player-settings--screen", fullscreen);
        popover.style.left = fullscreen ? "0" : `${Math.max(12, Math.min(innerWidth - bounds.width - 12, anchor.right - bounds.width))}px`;
        popover.style.top = fullscreen ? "0" : `${Math.max(12, Math.min(innerHeight - bounds.height - 12, anchor.top - bounds.height - 8))}px`;
    }
    root.querySelectorAll<HTMLElement>("[popover]").forEach(popover => popover.addEventListener("toggle", () => positionPopover(popover)));
    window.addEventListener("resize", () => root.querySelectorAll<HTMLElement>("[popover]:popover-open").forEach(positionPopover));

    root.addEventListener("click", event => {
        const button = (event.target as Element).closest<HTMLButtonElement>("button");
        if (!button || button.disabled) return;
        if (button.dataset.speed) change("speed", button.dataset.speed);
        if (button.dataset.fit) change("fit", button.dataset.fit);
        if (button.dataset.offset) change(button.dataset.offset, String(Math.max(-60000, Math.min(60000, Number(inherited(button.dataset.offset)) + Number(button.dataset.delta)))));
        if (button.dataset.resetSetting) {
            scopes.get(scope)!.delete(button.dataset.resetSetting);
            render();
        }
        if (button.dataset.action === "subtitle-toggle") {
            subtitle.setAttribute("aria-pressed", String(subtitle.getAttribute("aria-pressed") !== "true"));
            render();
        }
        if (button.dataset.action === "settings-page") location.hash = "player-settings-page";
        if (button.dataset.action === "settings-close") location.hash = "player-preview";
        if (button.dataset.copy) {
            const source = root.querySelector<HTMLSelectElement>("[data-copy-source]")!.value;
            const panes = Array.from(full.querySelectorAll<HTMLElement>(".player-preferences__controls > .player-settings__pane"));
            const selectedGroups = Array.from(full.querySelectorAll<HTMLInputElement>("[data-copy-groups] input")).map((input, index) => input.checked ? panes[index].id : "");
            const copiedPanes = panes.filter(pane => button.dataset.copy === "all" || (button.dataset.copy === "tab" ? !pane.hidden : selectedGroups.includes(pane.id)));
            const names = new Set(copiedPanes
                .flatMap(pane => Array.from(pane.querySelectorAll<HTMLInputElement>("[data-preview-setting]"), field => field.name)));
            if (copiedPanes.some(pane => pane.querySelector("[data-fit]"))) names.add("fit");
            // Never transfer file-specific tracks or corrections.
            names.forEach(name => { if (!corrections.has(name) && !["audioTrack", "primarySubtitle", "secondarySubtitle"].includes(name)) scopes.get(scope)!.set(name, inherited(name, source)); });
            render();
        }
    });
    showFull();
}

function fieldValue(field: HTMLInputElement | HTMLSelectElement): string {
    return field.type === "checkbox" ? String((field as HTMLInputElement).checked) : field.value;
}
