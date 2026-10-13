import "./comic.scss";
import { progressReporter } from "../Tracking/Progress";

// Comic reader: page or spread, reading direction, preloading and the exact page as progress.
interface ComicSettings {
    direction?: "ltr" | "rtl";
    spread: boolean;
}

const SettingsKey = "jularr.comic";
const Ahead = 3;
const Kept = 8;

const comic = document.querySelector<HTMLElement>(".comic");
if (comic) {
    const stage = comic.querySelector<HTMLElement>("[data-role=stage]")!;
    const position = comic.querySelector<HTMLElement>("[data-role=position]");
    const previous = comic.querySelector<HTMLAnchorElement>("[data-role=previous]");
    const next = comic.querySelector<HTMLAnchorElement>("[data-role=next]");
    const count = Number(comic.dataset.count);
    const pages = comic.dataset.pages!;
    const label = comic.dataset.pageLabel ?? "";
    const wide = new Set<number>();
    const loaded = new Map<number, HTMLImageElement>();
    let settings = load();
    let page = Number(comic.dataset.page);
    let moved = false;
    const save = progressReporter(comic.dataset.progressUrl!, comic.dataset.antiforgery!, failed => {
        const feedback = comic.querySelector<HTMLElement>("[data-role=save-error]");
        if (feedback) feedback.hidden = !failed;
    });

    render();

    // Buttons, keys and taps follow the reading direction.
    comic.querySelector<HTMLButtonElement>("[data-action=direction]")?.addEventListener("click", () => {
        settings.direction = direction() === "rtl" ? "ltr" : "rtl";
        store();
        render();
    });
    comic.querySelector<HTMLButtonElement>("[data-action=spread]")?.addEventListener("click", () => {
        settings.spread = !settings.spread;
        store();
        render();
    });
    previous?.addEventListener("click", event => { event.preventDefault(); go(-1); });
    next?.addEventListener("click", event => { event.preventDefault(); go(1); });
    document.addEventListener("keydown", event => {
        const target = event.target as HTMLElement | null;
        if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || target?.closest?.("a, button, input, select, textarea")) return;
        if (event.key === "ArrowLeft") go(direction() === "rtl" ? 1 : -1);
        else if (event.key === "ArrowRight") go(direction() === "rtl" ? -1 : 1);
        else if (event.key === " " || event.key === "PageDown") go(1);
        else if (event.key === "PageUp") go(-1);
        else return;
        event.preventDefault();
    });
    stage.addEventListener("click", event => {
        const left = event.clientX < stage.getBoundingClientRect().left + stage.clientWidth / 2;
        go(left === (direction() === "rtl") ? 1 : -1);
    });
    window.addEventListener("pagehide", () => { if (moved) report(true); });

    function direction(): "ltr" | "rtl" {
        return settings.direction ?? (comic!.dataset.direction === "rtl" ? "rtl" : "ltr");
    }

    // The cover and wide pages stand alone; other pages pair up in two-page mode.
    function shown(): number[] {
        if (!settings.spread || page === 0 || wide.has(page) || page + 1 >= count || wide.has(page + 1)) return [page];
        return [page, page + 1];
    }

    function go(step: number): void {
        const current = shown();
        const target = step > 0 ? current[current.length - 1] + 1 : page - 1;
        if (target < 0 || target >= count) return;
        page = step < 0 && settings.spread && target > 1 && !wide.has(target) && !wide.has(target - 1) ? target - 1 : target;
        moved = true;
        render();
        report(false);
    }

    function render(): void {
        const current = shown();
        const last = current[current.length - 1];
        stage.dataset.direction = direction();
        stage.dataset.spread = String(current.length > 1);
        stage.replaceChildren(...current.map(image));
        if (position) position.textContent = `${current.map(index => index + 1).join("–")} / ${count}`;
        if (previous) previous.hidden = page === 0;
        if (next) next.hidden = last + 1 >= count;
        comic!.querySelector<HTMLButtonElement>("[data-action=spread]")?.setAttribute("aria-pressed", String(settings.spread));
        const address = new URL(location.href);
        address.searchParams.set("p", String(page));
        history.replaceState(null, "", address.pathname + address.search);

        // The next pages load ahead, the previous one stays; far pages are released.
        for (let index = last + 1; index <= Math.min(count - 1, last + Ahead); index++) image(index);
        if (page > 0) image(page - 1);
        for (const index of loaded.keys()) {
            if (index < page - Kept || index > last + Kept) loaded.delete(index);
        }
    }

    function image(index: number): HTMLImageElement {
        let element = loaded.get(index);
        if (!element) {
            element = new Image();
            element.decoding = "async";
            element.alt = `${label} ${index + 1}`;
            element.src = pages + encodeURIComponent(`:page/${String(index).padStart(5, "0")}`);
            element.addEventListener("load", () => {
                if (element!.naturalWidth > element!.naturalHeight && !wide.has(index)) {
                    wide.add(index);
                    if (settings.spread && (index === page || index === page + 1)) render();
                }
            });
            loaded.set(index, element);
        }

        return element;
    }

    function report(leaving: boolean): void {
        const current = shown();
        const last = current[current.length - 1];
        save({ locator: `p${page}`, progression: Number(((last + 1) / count).toFixed(4)), isCompleted: last + 1 >= count }, leaving);
    }

    function load(): ComicSettings {
        try {
            const value = JSON.parse(localStorage.getItem(SettingsKey) ?? "{}") as Partial<ComicSettings> | null;
            return { spread: value?.spread ?? window.matchMedia("(orientation: landscape)").matches, direction: value?.direction };
        } catch {
            return { spread: false };
        }
    }

    function store(): void {
        try { localStorage.setItem(SettingsKey, JSON.stringify(settings)); } catch { /* Optional device storage. */ }
    }
}
