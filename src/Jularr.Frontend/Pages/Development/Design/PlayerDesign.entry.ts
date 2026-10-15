// Local presentation states, never playback APIs.
const workshop = document.querySelector<HTMLElement>("[data-player-workshop]");
if (workshop) initializePreview(workshop);

function initializePreview(root: HTMLElement): void {
    const player = root.querySelector<HTMLElement>(".player")!;
    const timeline = player.querySelector<HTMLInputElement>("[data-role=timeline]")!;
    const volume = player.querySelector<HTMLInputElement>("[data-role=volume]")!;
    const toggle = player.querySelector<HTMLButtonElement>("[data-action=toggle]")!;
    const duration = 1450;
    let position = 754;

    // Sample position stays user-controlled.
    timeline.max = String(duration);
    player.querySelector("[data-role=duration]")!.textContent = "24:10";
    player.querySelector<HTMLElement>("[data-action=minimize]")!.hidden = false;
    player.querySelector<HTMLElement>("[data-role=mode]")!.textContent = root.querySelector("[data-demo-feedback]")!.textContent;
    updatePosition(position);
    volume.style.setProperty("--played", "100%");

    function updatePosition(value: number): void {
        position = Math.max(0, Math.min(duration, value));
        timeline.value = String(position);
        timeline.style.setProperty("--played", `${position / duration * 100}%`);
        timeline.style.setProperty("--buffered", "76%");
        player.querySelector("[data-role=time]")!.textContent = `${Math.floor(position / 60)}:${String(position % 60).padStart(2, "0")}`;
    }

    function closePanels(): void {
        player.querySelectorAll<HTMLElement>("[data-panel]").forEach(panel => panel.hidden = true);
        player.querySelectorAll("[aria-expanded]").forEach(button => button.setAttribute("aria-expanded", "false"));
    }

    function minimize(value: boolean): void {
        closePanels();
        player.classList.toggle("player--mini", value);
        root.classList.toggle("player-workshop--mini", value);
    }

    timeline.addEventListener("input", () => updatePosition(Number(timeline.value)));
    volume.addEventListener("input", () => volume.style.setProperty("--played", `${Number(volume.value) * 100}%`));
    root.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            closePanels();
            minimize(false);
        }
    });

    root.addEventListener("click", event => {
        const target = event.target as Element;
        const button = target.closest<HTMLElement>("button, a[data-action]");
        if (!button || button.hasAttribute("disabled")) return;
        const action = button.dataset.action;
        if (action) event.preventDefault();

        // Panels use the real chrome.
        const panel = player.querySelector<HTMLElement>(`[data-panel="${action}"]`);
        if (panel) {
            const opening = panel.hidden;
            closePanels();
            panel.hidden = !opening;
            button.setAttribute("aria-expanded", String(opening));
            return;
        }

        const option = ["audio", "subtitle", "speed"].find(name => button.hasAttribute(`data-${name}`));
        if (option) {
            player.querySelectorAll(`[data-${option}]`).forEach(item => item.setAttribute("aria-checked", String(item === button)));
            if (option === "speed") player.querySelector("[data-role=speed-label]")!.textContent = button.dataset.speed + "×";
            closePanels();
        }

        if (action === "toggle") {
            const playing = player.classList.toggle("player--playing");
            toggle.setAttribute("aria-label", (playing ? toggle.dataset.labelPause : toggle.dataset.labelPlay)!);
        }
        if (action === "back") updatePosition(position - 10);
        if (action === "forward") updatePosition(position + 30);
        if (action === "mute") {
            const muted = player.classList.toggle("player--muted");
            button.setAttribute("aria-pressed", String(muted));
        }
        if (action === "minimize") minimize(true);
        if (["expand", "stop", "close"].includes(action ?? "")) {
            minimize(false);
            if (action !== "expand") {
                player.classList.remove("player--playing");
                toggle.setAttribute("aria-label", toggle.dataset.labelPlay!);
            }
        }
        if (action === "fullscreen") {
            void (document.fullscreenElement ? document.exitFullscreen() : player.requestFullscreen()).catch(() => undefined);
        }

        // Local episode and chapter samples.
        const episodes = Array.from(root.querySelectorAll<HTMLElement>("[data-episode]"));
        let episode = button.dataset.episode;
        if (action === "previous" || action === "next") {
            const selected = episodes.findIndex(item => item.getAttribute("aria-pressed") === "true");
            episode = episodes[Math.max(0, Math.min(episodes.length - 1, selected + (action === "next" ? 1 : -1)))].dataset.episode;
        }
        if (episode) {
            episodes.forEach(item => item.setAttribute("aria-pressed", String(item.dataset.episode === episode)));
            player.querySelector(".player__context")!.textContent = `S1 · E${episode}`;
            updatePosition(0);
        }
        if (button.dataset.chapter) updatePosition(Number(button.dataset.chapter));
    });
}
