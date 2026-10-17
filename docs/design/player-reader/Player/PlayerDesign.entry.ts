import { initializeSettings } from "./PlayerSettings";

// Presentation state only, never playback APIs.
const workshop = document.querySelector<HTMLElement>("[data-player-workshop]");
if (workshop) initializePreview(workshop);

function initializePreview(root: HTMLElement): void {
    const player = root.querySelector<HTMLElement>(".player")!;
    const timeline = player.querySelector<HTMLInputElement>("[data-role=timeline]")!;
    const volume = player.querySelector<HTMLInputElement>("[data-role=volume]")!;
    const toggle = player.querySelector<HTMLButtonElement>("[data-action=toggle]")!;
    const panel = root.querySelector<HTMLElement>("#player-settings");
    const duration = 1450;
    let position = 754;

    // Seek commits on release.
    timeline.max = String(duration);
    player.querySelector("[data-role=duration]")!.textContent = "24:10";
    player.querySelector<HTMLElement>("[data-action=minimize]")!.hidden = false;
    updatePosition(position);
    volume.style.setProperty("--played", "100%");
    timeline.addEventListener("change", () => updatePosition(Number(timeline.value)));
    volume.addEventListener("input", () => volume.style.setProperty("--played", `${Number(volume.value) * 100}%`));
    if (panel) {
        initializeSettings(root, player);
        initializeAutoHide(root, player);
    }
    initializeTimeline(root, timeline, duration);
    initializeTabs(root, "[data-sidebar-tabs]", ["player-episodes", "player-details"]);

    function updatePosition(value: number): void {
        position = Math.max(0, Math.min(duration, value));
        timeline.value = String(position);
        timeline.style.setProperty("--played", `${position / duration * 100}%`);
        timeline.style.setProperty("--buffered", "76%");
        player.querySelector("[data-role=time]")!.textContent = clock(position);
    }

    function minimize(value: boolean): void {
        root.querySelectorAll<HTMLElement>("[popover]:popover-open").forEach(item => item.hidePopover());
        player.classList.toggle("player--mini", value);
        root.classList.toggle("player-workshop--mini", value);
    }

    root.addEventListener("click", event => {
        const button = (event.target as Element).closest<HTMLElement>("button, a[data-action]");
        if (!button || button.hasAttribute("disabled")) return;
        const action = button.dataset.action;
        if (action && !button.hasAttribute("popovertarget")) event.preventDefault();
        if (action === "toggle") {
            const playing = player.classList.toggle("player--playing");
            toggle.setAttribute("aria-label", (playing ? toggle.dataset.labelPause : toggle.dataset.labelPlay)!);
        }
        if (action === "back") updatePosition(position - 10);
        if (action === "forward") updatePosition(position + 30);
        if (action === "mute") button.setAttribute("aria-pressed", String(player.classList.toggle("player--muted")));
        if (action === "minimize" || action === "close") minimize(true);
        if (action === "expand" || action === "stop") minimize(false);
        if (action === "stop") {
            player.classList.remove("player--playing");
            toggle.setAttribute("aria-label", toggle.dataset.labelPlay!);
        }
        if (action === "theater") button.setAttribute("aria-pressed", String(root.classList.toggle("player-workshop--theater")));
        if (action === "fullscreen") {
            const stage = root.querySelector<HTMLElement>(".player-workshop__stage")!;
            void (document.fullscreenElement ? document.exitFullscreen() : stage.requestFullscreen()).catch(() => undefined);
        }

        // Local episode and chapter selection.
        const episodes = Array.from(root.querySelectorAll<HTMLElement>("[data-episode]"));
        let episode = button.dataset.episode;
        if (action === "previous" || action === "next") {
            const selected = episodes.findIndex(item => item.getAttribute("aria-pressed") === "true");
            episode = episodes[Math.max(0, Math.min(episodes.length - 1, selected + (action === "next" ? 1 : -1)))].dataset.episode;
        }
        if (episode) {
            episodes.forEach(item => item.setAttribute("aria-pressed", String(item.dataset.episode === episode)));
            const season = root.querySelector<HTMLSelectElement>("[data-sample-season]")?.value ?? "1";
            player.querySelector(".player__context")!.textContent = `S${season} · E${episode}`;
            updatePosition(0);
        }
        if (button.dataset.chapter) updatePosition(Number(button.dataset.chapter));
    });
    root.querySelector("[data-sample-season]")?.addEventListener("change", event => {
        const episode = root.querySelector<HTMLElement>('[data-episode][aria-pressed="true"]')!.dataset.episode;
        player.querySelector(".player__context")!.textContent = `S${(event.target as HTMLSelectElement).value} · E${episode}`;
    });
}

function initializeAutoHide(root: HTMLElement, player: HTMLElement): void {
    let timer: ReturnType<typeof setTimeout>;
    function awake(): void {
        clearTimeout(timer);
        player.classList.remove("player--idle");
        timer = setTimeout(() => {
            if (player.classList.contains("player--playing") && !root.querySelector(":popover-open") && !player.querySelector(":focus-visible")) player.classList.add("player--idle");
        }, 3000);
    }
    ["pointermove", "pointerdown", "keydown", "focusin", "click"].forEach(name => root.addEventListener(name, awake));
    root.querySelectorAll("[popover]").forEach(item => item.addEventListener("toggle", awake));
}

function initializeTabs(root: HTMLElement, selector: string, panes: string[]): void {
    const tabs = root.querySelector<HTMLElement>(selector);
    tabs?.addEventListener("click", event => {
        const link = (event.target as Element).closest<HTMLAnchorElement>("a");
        if (!link) return;
        event.preventDefault();
        tabs.querySelectorAll("a").forEach(item => {
            item.classList.toggle("navigation-tabs__link--active", item === link);
            if (item === link) item.setAttribute("aria-current", "page");
            else item.removeAttribute("aria-current");
        });
        panes.forEach(id => root.querySelector<HTMLElement>(`#${id}`)!.hidden = link.hash !== `#${id}`);
    });
}

function initializeTimeline(root: HTMLElement, timeline: HTMLInputElement, duration: number): void {
    const markers = root.querySelector<HTMLElement>("[data-timeline-markers]");
    const content = root.querySelector<HTMLElement>("[data-seek-preview]");
    if (!markers || !content) return;
    const track = timeline.parentElement!;
    const preview = track.querySelector<HTMLElement>("[data-role=preview]")!;
    preview.append(content);
    content.hidden = false;
    track.append(markers);
    markers.hidden = false;

    function place(): void {
        const bounds = timeline.getBoundingClientRect();
        markers!.style.left = `${bounds.left - track.getBoundingClientRect().left}px`;
        markers!.style.width = `${bounds.width}px`;
        markers!.querySelectorAll<HTMLElement>("[data-chapter]").forEach(marker => marker.style.left = `${Number(marker.dataset.chapter) / duration * 100}%`);
    }
    place();
    window.addEventListener("resize", place);
    timeline.addEventListener("pointerenter", place);
    function showPreview(x: number, seconds: number): void {
        content!.querySelector("[data-seek-time]")!.textContent = clock(seconds);
        preview.hidden = false;
        preview.style.left = `${Math.max(0, Math.min(track.clientWidth - 160, x - track.getBoundingClientRect().left - 80))}px`;
    }
    timeline.addEventListener("pointermove", event => {
        const bounds = timeline.getBoundingClientRect();
        const ratio = Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width));
        showPreview(event.clientX, Math.round(ratio * duration));
    });
    timeline.addEventListener("pointerleave", () => preview.hidden = true);
    markers.querySelectorAll<HTMLElement>("[data-chapter]").forEach(marker => {
        const show = () => showPreview(marker.getBoundingClientRect().left + 3, Number(marker.dataset.chapter));
        marker.addEventListener("pointerenter", show);
        marker.addEventListener("focus", show);
        marker.addEventListener("pointerleave", () => preview.hidden = true);
        marker.addEventListener("blur", () => preview.hidden = true);
    });
}

function clock(seconds: number): string {
    return `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, "0")}`;
}
