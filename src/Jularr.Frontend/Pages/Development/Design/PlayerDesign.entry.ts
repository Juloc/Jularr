// Presentation state only. Never call playback, profile or translation APIs here.
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

    // Seek commits on release, playback state stays unchanged.
    timeline.max = String(duration);
    player.querySelector("[data-role=duration]")!.textContent = "24:10";
    player.querySelector<HTMLElement>("[data-action=minimize]")!.hidden = false;
    updatePosition(position);
    volume.style.setProperty("--played", "100%");
    timeline.addEventListener("change", () => updatePosition(Number(timeline.value)));
    volume.addEventListener("input", () => volume.style.setProperty("--played", `${Number(volume.value) * 100}%`));
    if (panel) initializeSettings(root, player, panel);
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

        // Episode and chapter samples.
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

function initializeSettings(root: HTMLElement, player: HTMLElement, panel: HTMLElement): void {
    const fields = Array.from(panel.querySelectorAll<HTMLInputElement | HTMLSelectElement>("[data-preview-setting]"));
    const defaults = new Map(fields.map(field => [field.name, fieldValue(field)]));
    const changed = new Map<string, string>();
    const save = panel.querySelector<HTMLButtonElement>('[data-action="save-defaults"]')!;
    const captions = root.querySelector<HTMLElement>(".player-workshop__captions")!;
    const subtitle = player.querySelector<HTMLElement>('[data-action="subtitle-toggle"]')!;
    const feedback = panel.querySelector<HTMLElement>("[data-settings-feedback]")!;
    const field = (name: string) => fields.find(item => item.name === name)!;
    const value = (name: string) => fieldValue(field(name));
    defaults.set("subtitleEnabled", subtitle.getAttribute("aria-pressed") ?? "false");

    function remember(name: string, current: string): void {
        if (defaults.get(name) === current) changed.delete(name);
        else changed.set(name, current);
        save.disabled = changed.size === 0;
        save.dataset.dirtyCount = String(changed.size);
    }

    // Both tracks remain one caption group.
    function appearance(): void {
        captions.hidden = subtitle.getAttribute("aria-pressed") !== "true";
        captions.style.bottom = `max(100px, ${value("bottomOffset")}%)`;
        captions.style.maxWidth = `${value("maxWidth")}%`;
        captions.style.fontFamily = value("font") === "system" ? "inherit" : value("font");
        captions.style.setProperty("--caption-size", `${value("fontSize")}px`);
        captions.style.setProperty("--caption-color", value("textColor"));
        captions.style.setProperty("--caption-background", String(Number(value("backgroundOpacity")) / 100));
        captions.style.setProperty("--caption-opacity", String(Number(value("textOpacity")) / 100));
        captions.style.setProperty("--caption-outline", `${value("outline")}px`);
        captions.style.setProperty("--caption-shadow", value("shadow") === "true" ? "0 2px 4px #000" : "none");
        captions.querySelector<HTMLElement>('[data-caption="primary"]')!.hidden = value("primarySubtitle") === "";
        const secondary = captions.querySelector<HTMLElement>('[data-caption="secondary"]')!;
        secondary.hidden = value("secondarySubtitle") === "";
        secondary.style.fontSize = value("separateStyle") === "true" ? `${value("secondaryFontSize")}px` : "inherit";
        secondary.style.color = value("separateStyle") === "true" ? value("secondaryColor") : "inherit";
        fields.filter(item => item.type === "range").forEach(item => {
            item.setAttribute("aria-valuetext", item.value);
            panel.querySelector(`[data-range-value="${item.name}"]`)!.textContent = item.value;
        });
    }

    function change(input: HTMLInputElement | HTMLSelectElement): void {
        remember(input.name, fieldValue(input));
        if (input.name === "subtitlePolicy") {
            subtitle.setAttribute("aria-pressed", String(input.value !== "off"));
            remember("subtitleEnabled", String(input.value !== "off"));
        }
        const video = player.querySelector<HTMLVideoElement>("video")!;
        video.style.objectFit = value("fit") === "zoom" ? "cover" : value("fit");
        video.style.transform = value("fit") === "zoom" ? "scale(1.15)" : "none";
        panel.querySelectorAll<HTMLElement>("[data-speed]").forEach(button => button.setAttribute("aria-pressed", String(button.dataset.speed === value("speed"))));
        field("sleepMinutes").disabled = value("sleep") !== "custom";
        appearance();
    }
    fields.forEach(input => input.addEventListener("input", () => change(input)));
    appearance();
    field("sleepMinutes").disabled = true;
    initializeTabs(panel, "[data-settings-tabs]", ["settings-audio", "settings-subtitles", "settings-playback", "settings-info"]);

    // Native popovers own outside-click, Escape and focus return.
    root.querySelectorAll<HTMLElement>("[popover]").forEach(popover => popover.addEventListener("toggle", event => {
        if ((event as ToggleEvent).newState !== "open") return;
        const anchor = root.querySelector<HTMLElement>(`[popovertarget="${popover.id}"]`)!.getBoundingClientRect();
        const bounds = popover.getBoundingClientRect();
        popover.style.left = `${Math.max(8, Math.min(innerWidth - bounds.width - 8, anchor.right - bounds.width))}px`;
        popover.style.top = `${Math.max(8, Math.min(innerHeight - bounds.height - 8, anchor.top - bounds.height - 8))}px`;
    }));

    root.addEventListener("click", event => {
        const button = (event.target as Element).closest<HTMLElement>("button");
        if (!button) return;
        if (button.dataset.speed) {
            field("speed").value = button.dataset.speed;
            change(field("speed"));
        }
        if (button.dataset.offset) {
            const input = field(button.dataset.offset);
            input.value = button.dataset.delta === "0" ? "0" : String(Number(input.value) + Number(button.dataset.delta));
            change(input);
        }
        const action = button.dataset.action;
        if (action === "subtitle-toggle") {
            const enabled = subtitle.getAttribute("aria-pressed") !== "true";
            subtitle.setAttribute("aria-pressed", String(enabled));
            remember("subtitleEnabled", String(enabled));
            appearance();
        }
        if (action === "save-defaults") {
            changed.forEach((current, name) => defaults.set(name, current));
            changed.clear();
            save.disabled = true;
            save.dataset.dirtyCount = "0";
            feedback.textContent = root.querySelector('[data-message="saved"]')!.textContent;
        }
        if (action === "translate-preview" || action === "cast-preview") feedback.textContent = root.querySelector('[data-message="local"]')!.textContent;
        if (action === "lock-preview" || action === "unlock-preview") {
            const locked = action === "lock-preview";
            root.classList.toggle("player-workshop--locked", locked);
            root.querySelector<HTMLElement>('[data-action="unlock-preview"]')!.hidden = !locked;
            if (panel.matches(":popover-open")) panel.hidePopover();
        }
    });
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

function fieldValue(field: HTMLInputElement | HTMLSelectElement): string {
    return field.type === "checkbox" ? String((field as HTMLInputElement).checked) : field.value;
}

function clock(seconds: number): string {
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}
