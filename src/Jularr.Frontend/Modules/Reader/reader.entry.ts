import "./reader.scss";
import { progressReporter } from "../Tracking/Progress";
import { findings, highlights } from "./highlights";
import { translator } from "./Translation/translation";
import { createTranslationWorker } from "./Translation/worker";

// Reader client: settings, exact block position and progress reporting.
interface ReaderSettings {
    size: number;
    theme: "light" | "sepia" | "dark";
    mode: "scroll" | "paged";
    preset: "book" | "light-novel" | "custom";
    font: "serif" | "sans";
    lineHeight: "compact" | "normal" | "spacious";
    width: "narrow" | "normal" | "wide";
}

const SettingsKey = "jularr.reader";
const ReportDelay = 1500;
const Defaults: ReaderSettings = { size: 1.1, theme: "light", mode: "scroll", preset: "book", font: "serif", lineHeight: "normal", width: "normal" };

// The PDF view shares only the reader's look.
const reader = document.querySelector<HTMLElement>(".reader");
if (reader?.querySelector("[data-role=content]")) {
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
    const allBlocks = Array.from(content.querySelectorAll<HTMLElement>("[data-b]"));
    const save = progressReporter(reader.dataset.progressUrl!, reader.dataset.antiforgery!, failed => {
        const feedback = reader.querySelector<HTMLElement>('[data-role=save-error]');
        if (feedback) feedback.hidden = !failed;
    });

    apply();
    restore();
    highlights(reader!, content, section);
    if (reader!.dataset.query) findings(content, reader!.dataset.query);

    // Translation where the instance offers it, in the language the profile chose for this book; the choice follows it to every device.
    const translate = reader.dataset.translationUrl
        ? translator(reader, content, createTranslationWorker)
        : undefined;
    let reading = reader.dataset.readingLanguage ?? "original";
    if (translate) void translate(reading === "original" ? "" : reading);
    reader.querySelectorAll<HTMLButtonElement>("[data-language-choice]").forEach(button => button.addEventListener("click", () => {
        reading = button.dataset.languageChoice!;
        reader!.querySelectorAll<HTMLButtonElement>("[data-language-choice]").forEach(choice => choice.setAttribute("aria-pressed", String(choice === button)));
        const opener = reader!.querySelector<HTMLButtonElement>("[data-action=language]");
        if (opener) opener.textContent = button.textContent;
        void fetch(reader!.dataset.languageUrl!, {
            method: "PUT", credentials: "same-origin",
            headers: { "Content-Type": "application/json", RequestVerificationToken: reader!.dataset.antiforgery! },
            body: JSON.stringify({ readingLanguage: reading })
        });
        if (translate) void translate(reading === "original" ? "" : reading);
    }));

    // Panels and settings.
    reader.querySelectorAll<HTMLButtonElement>("[data-action]").forEach(button => button.addEventListener("click", () => toggle(button)));
    reader.querySelectorAll<HTMLButtonElement>("[data-setting]").forEach(button => button.addEventListener("click", () => {
        const value = button.dataset.value!;
        if (button.dataset.setting === "preset") {
            settings.preset = value as ReaderSettings["preset"];
            settings.font = "serif";
            settings.lineHeight = value === "light-novel" ? "spacious" : "normal";
            settings.width = value === "light-novel" ? "narrow" : "normal";
            settings.size = Defaults.size;
        }
        if (button.dataset.setting === "size") settings.size = Math.min(2, Math.max(0.8, settings.size + (value === "larger" ? 0.1 : -0.1)));
        if (button.dataset.setting === "font") settings.font = value as ReaderSettings["font"];
        if (button.dataset.setting === "lineHeight") settings.lineHeight = value as ReaderSettings["lineHeight"];
        if (button.dataset.setting === "width") settings.width = value as ReaderSettings["width"];
        if (["size", "font", "lineHeight", "width"].includes(button.dataset.setting ?? "")) settings.preset = "custom";
        if (button.dataset.setting === "theme") settings.theme = value as ReaderSettings["theme"];
        if (button.dataset.setting === "mode") settings.mode = value as ReaderSettings["mode"];
        apply();
        try { localStorage.setItem(SettingsKey, JSON.stringify(settings)); } catch { /* Optional device storage. */ }
    }));

    // Paged mode turns pages; the edges continue to the neighbouring section.
    document.addEventListener("keydown", event => {
        if (event.key === "Escape" && reader!.classList.contains("reader--focused")) {
            event.preventDefault();
            setFocusMode(false);
            return;
        }
        if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement) return;
        if (event.key === "ArrowRight" || event.key === "PageDown") page(1, event);
        if (event.key === "ArrowLeft" || event.key === "PageUp") page(-1, event);
    });
    content.addEventListener("click", event => {
        if (settings.mode !== "paged" || (event.target as HTMLElement).closest("a, mark")) return;
        page(event.clientX > window.innerWidth / 2 ? 1 : -1, event);
    });

    content.addEventListener("scroll", schedule, { passive: true });
    window.addEventListener("scroll", schedule, { passive: true });
    window.addEventListener("pagehide", () => report(true));

    function load(): ReaderSettings {
        try {
            const value = JSON.parse(localStorage.getItem(SettingsKey) ?? "{}") as Partial<ReaderSettings> | null;
            return {
                size: typeof value?.size === 'number' && Number.isFinite(value.size) ? Math.min(2, Math.max(0.8, value.size)) : Defaults.size,
                theme: value?.theme === 'sepia' || value?.theme === 'dark' ? value.theme : Defaults.theme,
                mode: value?.mode === 'paged' ? value.mode : Defaults.mode,
                preset: value?.preset === "light-novel" || value?.preset === "custom" ? value.preset : Defaults.preset,
                font: value?.font === "sans" ? value.font : Defaults.font,
                lineHeight: value?.lineHeight === "compact" || value?.lineHeight === "spacious" ? value.lineHeight : Defaults.lineHeight,
                width: value?.width === "narrow" || value?.width === "wide" ? value.width : Defaults.width
            };
        } catch {
            return { ...Defaults };
        }
    }

    function apply() {
        reader!.dataset.theme = settings.theme;
        reader!.dataset.mode = settings.mode;
        reader!.style.setProperty("--reader-size", `${settings.size.toFixed(1)}rem`);
        reader!.style.setProperty("--reader-font-family", settings.font === "sans" ? "system-ui, sans-serif" : 'Georgia, "Iowan Old Style", "Times New Roman", serif');
        reader!.style.setProperty("--reader-line-height", { compact: "1.45", normal: "1.65", spacious: "1.9" }[settings.lineHeight]);
        reader!.style.setProperty("--reader-width", { narrow: "34rem", normal: "42rem", wide: "55rem" }[settings.width]);
        reader!.querySelectorAll<HTMLButtonElement>("[data-setting]").forEach(button => {
            const selected = settings[button.dataset.setting as keyof ReaderSettings];
            const active = button.dataset.value === selected;
            button.setAttribute("aria-pressed", String(active));
        });
    }

    function setFocusMode(enabled: boolean): void {
        reader!.classList.toggle("reader--focused", enabled);
        reader!.querySelectorAll<HTMLElement>("[data-panel]").forEach(panel => { panel.hidden = true; });
        reader!.querySelectorAll<HTMLElement>("[aria-expanded]").forEach(control => control.setAttribute("aria-expanded", "false"));
        const restore = reader!.querySelector<HTMLButtonElement>("[data-action=restore-focus]");
        if (restore) {
            restore.hidden = !enabled;
            if (enabled) restore.focus();
        }
        if (!enabled) reader!.querySelector<HTMLButtonElement>("[data-action=focus]")?.focus();
    }

    function toggle(button: HTMLButtonElement) {
        const name = button.dataset.action!;
        if (name === "focus" || name === "restore-focus") {
            setFocusMode(name === "focus");
            return;
        }
        reader!.querySelectorAll<HTMLElement>("[data-panel]").forEach(panel => {
            const open = panel.dataset.panel === name && panel.hidden;
            panel.hidden = !open;
            reader!.querySelector(`[data-action=${panel.dataset.panel}]`)?.setAttribute("aria-expanded", String(open));
        });
    }

    // A note's block, else the saved block of this section, unless the address names an anchor.
    function restore() {
        const requested = new URLSearchParams(location.search).get("b");
        const linked = requested && /^\d+$/.test(requested) ? requested : null;
        const match = /^s(\d+)\/b(\d+)$/.exec(reader!.dataset.locator ?? "");
        const block = linked ?? (match && Number(match[1]) === section ? match[2] : null);
        if (location.hash || block === null) return;
        content.querySelector(`[data-b="${block}"]`)?.scrollIntoView({ block: "start" });
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
            content.scrollBy({ left: direction * width, behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' });
        }
    }

    function current(): number {
        // First block whose top is visible.
        const bounds = content.getBoundingClientRect();
        const top = reader!.querySelector('.reader__top')?.getBoundingClientRect().bottom ?? 0;
        for (const block of allBlocks) {
            const box = block.getBoundingClientRect();
            if (settings.mode === "paged" ? box.right > bounds.left + 1 : box.bottom > top + 1) return Number(block.dataset.b);
        }

        return allBlocks.length > 0 ? Number(allBlocks[allBlocks.length - 1].dataset.b) : 0;
    }

    function schedule() {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => report(false), ReportDelay);
    }

    function report(leaving: boolean) {
        const block = current();
        // Completion requires the section end.
        const bounds = content.getBoundingClientRect();
        const atEnd = allBlocks.length > 0 && (settings.mode === 'paged'
            ? content.scrollLeft + content.clientWidth >= content.scrollWidth - 4
            : bounds.bottom <= window.innerHeight);
        const completed = section === sections - 1 && atEnd;
        const progression = completed ? 1 : Math.min(1, (before + characters * (block / blocks)) / total);
        if (position) position.textContent = `${Math.round(progression * 100)} %`;
        save({ locator: `s${section}/b${block}`, progression: Number(progression.toFixed(4)), isCompleted: completed }, leaving);
    }
}
