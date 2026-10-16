import "./reader.scss";
import { progressReporter } from "../Tracking/Progress";
import { findings, highlights } from "./highlights";
import { translator, type Language } from "./Translation/translation";
import { createTranslationWorker } from "./Translation/worker";

// Reader client: settings, exact block position and progress reporting.
interface ReaderSettings {
    size: number;
    theme: "light" | "sepia" | "dark";
    mode: "scroll" | "paged";
    // A language, the original, or unset: then the profile's language.
    translate: Language | "original" | "";
}

const SettingsKey = "jularr.reader";
const ReportDelay = 1500;
const Defaults: ReaderSettings = { size: 1.1, theme: "light", mode: "scroll", translate: "" };

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

    // Translation where the instance offers it, in the language chosen on this device.
    const translate = reader.dataset.translationUrl
        ? translator(reader, content, createTranslationWorker)
        : undefined;
    if (translate) void translate(target(settings.translate));

    // Panels and settings.
    reader.querySelectorAll<HTMLButtonElement>("[data-action]").forEach(button => button.addEventListener("click", () => toggle(button)));
    reader.querySelectorAll<HTMLButtonElement>("[data-setting]").forEach(button => button.addEventListener("click", () => {
        const value = button.dataset.value!;
        if (button.dataset.setting === "size") settings.size = Math.min(2, Math.max(0.8, settings.size + (value === "larger" ? 0.1 : -0.1)));
        if (button.dataset.setting === "theme") settings.theme = value as ReaderSettings["theme"];
        if (button.dataset.setting === "mode") settings.mode = value as ReaderSettings["mode"];
        if (button.dataset.setting === "translate" && translate) {
            settings.translate = value as ReaderSettings["translate"];
            void translate(target(settings.translate));
        }
        apply();
        try { localStorage.setItem(SettingsKey, JSON.stringify(settings)); } catch { /* Optional device storage. */ }
    }));

    // Paged mode turns pages; the edges continue to the neighbouring section.
    document.addEventListener("keydown", event => {
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

    // Unset follows the profile's language; the original stays untranslated.
    function target(choice: ReaderSettings["translate"]): Language | "" {
        if (choice === "original") return "";
        return choice || (reader!.dataset.profileLanguage as Language | undefined) || "";
    }

    function load(): ReaderSettings {
        try {
            const value = JSON.parse(localStorage.getItem(SettingsKey) ?? "{}") as Partial<ReaderSettings> | null;
            return {
                size: typeof value?.size === 'number' && Number.isFinite(value.size) ? Math.min(2, Math.max(0.8, value.size)) : Defaults.size,
                theme: value?.theme === 'sepia' || value?.theme === 'dark' ? value.theme : Defaults.theme,
                mode: value?.mode === 'paged' ? value.mode : Defaults.mode,
                translate: value?.translate === 'English' || value?.translate === 'German' || value?.translate === 'Indonesian' || value?.translate === 'Japanese'
                    || value?.translate === 'original' ? value.translate : Defaults.translate
            };
        } catch {
            return { ...Defaults };
        }
    }

    function apply() {
        reader!.dataset.theme = settings.theme;
        reader!.dataset.mode = settings.mode;
        reader!.style.setProperty("--reader-size", `${settings.size.toFixed(1)}rem`);
        reader!.querySelectorAll<HTMLButtonElement>("[data-setting]").forEach(button => {
            const active = (button.dataset.setting === "theme" && button.dataset.value === settings.theme) || (button.dataset.setting === "mode" && button.dataset.value === settings.mode)
                || (button.dataset.setting === "translate" && button.dataset.value === (target(settings.translate) || "original"));
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
