import "./reader.scss";

// Reader client: settings, exact block position and progress reporting.
interface ReaderSettings {
    size: number;
    theme: "light" | "sepia" | "dark";
    mode: "scroll" | "paged";
}

const SettingsKey = "jularr.reader";
const ReportDelay = 1500;
const Defaults: ReaderSettings = { size: 1.1, theme: "light", mode: "scroll" };

const reader = document.querySelector<HTMLElement>(".reader");
if (reader) {
    const content = reader.querySelector<HTMLElement>("[data-role=content]")!;
    const position = reader.querySelector<HTMLElement>("[data-role=position]");
    const section = Number(reader.dataset.section);
    const blocks = Math.max(1, Number(reader.dataset.blocks));
    const before = Number(reader.dataset.before);
    const characters = Number(reader.dataset.characters);
    const total = Math.max(1, Number(reader.dataset.total));
    const sections = Number(reader.dataset.sections);
    let settings = load();
    let timer = 0;
    let lastLocator = "";

    apply();
    restore();

    // Panels and settings.
    reader.querySelectorAll<HTMLButtonElement>("[data-action]").forEach(button => button.addEventListener("click", () => toggle(button)));
    reader.querySelectorAll<HTMLButtonElement>("[data-setting]").forEach(button => button.addEventListener("click", () => {
        const value = button.dataset.value!;
        if (button.dataset.setting === "size") settings.size = Math.min(2, Math.max(0.8, settings.size + (value === "larger" ? 0.1 : -0.1)));
        if (button.dataset.setting === "theme") settings.theme = value as ReaderSettings["theme"];
        if (button.dataset.setting === "mode") settings.mode = value as ReaderSettings["mode"];
        localStorage.setItem(SettingsKey, JSON.stringify(settings));
        apply();
    }));

    // Paged mode turns pages; the edges continue to the neighbouring section.
    document.addEventListener("keydown", event => {
        if (event.target instanceof HTMLInputElement) return;
        if (event.key === "ArrowRight" || event.key === "PageDown") page(1, event);
        if (event.key === "ArrowLeft" || event.key === "PageUp") page(-1, event);
    });
    content.addEventListener("click", event => {
        if (settings.mode !== "paged" || (event.target as HTMLElement).closest("a")) return;
        page(event.clientX > window.innerWidth / 2 ? 1 : -1, event);
    });

    content.addEventListener("scroll", schedule, { passive: true });
    window.addEventListener("scroll", schedule, { passive: true });
    window.addEventListener("pagehide", () => report(true));

    function load(): ReaderSettings {
        try {
            return { ...Defaults, ...JSON.parse(localStorage.getItem(SettingsKey) ?? "{}") };
        } catch {
            return { ...Defaults };
        }
    }

    function apply() {
        reader!.dataset.theme = settings.theme;
        reader!.dataset.mode = settings.mode;
        reader!.style.setProperty("--reader-size", `${settings.size.toFixed(1)}rem`);
        reader!.querySelectorAll<HTMLButtonElement>("[data-setting]").forEach(button => {
            const active = (button.dataset.setting === "theme" && button.dataset.value === settings.theme) || (button.dataset.setting === "mode" && button.dataset.value === settings.mode);
            button.setAttribute("aria-pressed", String(active));
        });
    }

    function toggle(button: HTMLButtonElement) {
        const name = button.dataset.action!;
        reader!.querySelectorAll<HTMLElement>("[data-panel]").forEach(panel => {
            const open = panel.dataset.panel === name && panel.hidden;
            panel.hidden = !open;
            reader!.querySelector(`[data-action=${panel.dataset.panel}]`)?.setAttribute("aria-expanded", String(open));
        });
    }

    // The saved block of this section, unless the address names an anchor.
    function restore() {
        const match = /^s(\d+)\/b(\d+)$/.exec(reader!.dataset.locator ?? "");
        if (location.hash || !match || Number(match[1]) !== section) return;
        content.querySelector(`[data-b="${match[2]}"]`)?.scrollIntoView({ block: "start" });
    }

    function page(direction: number, event: Event) {
        if (settings.mode !== "paged") return;
        event.preventDefault();
        const width = content.clientWidth;
        const atEnd = content.scrollLeft + width >= content.scrollWidth - 4;
        const atStart = content.scrollLeft <= 4;
        if (direction > 0 && atEnd) {
            reader!.querySelector<HTMLAnchorElement>("[data-role=next]")?.click();
        } else if (direction < 0 && atStart) {
            reader!.querySelector<HTMLAnchorElement>("[data-role=previous]")?.click();
        } else {
            content.scrollBy({ left: direction * width, behavior: "smooth" });
        }
    }

    function current(): number {
        // First block whose top is visible.
        const all = Array.from(content.querySelectorAll<HTMLElement>("[data-b]"));
        for (const block of all) {
            const box = block.getBoundingClientRect();
            if (settings.mode === "paged" ? box.right > content.getBoundingClientRect().left + 1 : box.bottom > 1) return Number(block.dataset.b);
        }

        return all.length > 0 ? Number(all[all.length - 1].dataset.b) : 0;
    }

    function schedule() {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => report(false), ReportDelay);
    }

    function report(leaving: boolean) {
        const block = current();
        const locator = `s${section}/b${block}`;
        if (locator === lastLocator) return;
        lastLocator = locator;

        const progression = Math.min(1, (before + characters * (block / blocks)) / total);
        if (position) position.textContent = `${Math.round(progression * 100)} %`;
        const completed = section === sections - 1 && block >= blocks - 1;
        void fetch(reader!.dataset.progressUrl!, {
            method: "PUT",
            keepalive: leaving,
            headers: { "Content-Type": "application/json", RequestVerificationToken: reader!.dataset.antiforgery! },
            body: JSON.stringify({ locator, progression: Number(progression.toFixed(4)), isCompleted: completed })
        });
    }
}
