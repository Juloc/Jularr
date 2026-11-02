// Live values without a reload. The server streams what changed as Server-Sent Events; pages mark what they show:
//   data-live="kind:id"       the element (or block) shows that entity; data-live-optional hides it while the stream knows nothing for it
//   data-live-text="name"     its text is the entity's text of that name, hidden while there is none
//   data-live-value="percent" a <progress> takes the entity's percent, hidden while there is none
//   data-live-points="name"   an SVG line takes the entity's text of that name as its points (a speed line), left as it is while there is none
// The entity's phase and step become data-phase and data-step, which the styles read. Nothing else on the page is touched.

export interface LiveEntity {
    phase?: string;
    step?: number;
    percent?: number | null;
    text?: Record<string, string>;
}

export type LiveEntities = Record<string, LiveEntity>;

const Address = "/live/events";
const FirstDelay = 1000;
const LastDelay = 30000;

// Patches the marked elements below root with what the stream knows.
export function applyLive(root: ParentNode, entities: LiveEntities): void {
    for (const element of Array.from(root.querySelectorAll<HTMLElement>("[data-live]"))) {
        const entity = entities[element.dataset.live ?? ""];
        const optional = element.hasAttribute("data-live-optional");
        if (!entity) {
            if (optional) element.hidden = true;
            continue;
        }

        if (optional) element.hidden = false;
        setAttribute(element, "data-phase", entity.phase);
        setAttribute(element, "data-step", entity.step === undefined ? undefined : String(entity.step));
        for (const line of Array.from(element.querySelectorAll<SVGElement>("[data-live-points]"))) {
            const points = entity.text?.[line.dataset.livePoints ?? ""];
            if (points) setAttribute(line as unknown as HTMLElement, "points", points);
        }

        const fields = Array.from(element.querySelectorAll<HTMLElement>("[data-live-text], [data-live-value]"));
        if (element.hasAttribute("data-live-text") || element.hasAttribute("data-live-value")) fields.push(element);
        for (const field of fields) {
            const name = field.dataset.liveText;
            if (name !== undefined) {
                const text = entity.text?.[name] ?? "";
                if (field.textContent !== text) field.textContent = text;
                field.hidden = text === "";
            }

            if (field.dataset.liveValue === "percent") {
                const percent = entity.percent;
                if (percent === undefined || percent === null) {
                    field.removeAttribute("value");
                    field.hidden = true;
                } else {
                    field.setAttribute("value", String(percent));
                    field.hidden = false;
                }
            }
        }
    }
}

function setAttribute(element: HTMLElement, name: string, value: string | undefined): void {
    if (value === undefined) return;
    if (element.getAttribute(name) !== value) element.setAttribute(name, value);
}

let source: EventSource | undefined;
let retry: ReturnType<typeof setTimeout> | undefined;
let delay = FirstDelay;
let latest: LiveEntities | undefined;

const wanted = () => !document.hidden && document.querySelector("#app-content [data-live]") !== null;

// Opens the stream while the visible page shows live values and closes it otherwise; every opening starts with a full snapshot.
export function syncLive(): void {
    if (!wanted()) {
        close();
        return;
    }

    if (latest) applyLive(document, latest);
    if (!source && retry === undefined) open();
}

function open(): void {
    const stream = new EventSource(Address);
    source = stream;
    const received = (event: Event) => {
        delay = FirstDelay;
        latest = (JSON.parse((event as MessageEvent<string>).data) as { entities: LiveEntities }).entities;
        applyLive(document, latest);
    };
    stream.addEventListener("snapshot", received);
    stream.addEventListener("update", received);
    stream.addEventListener("error", () => {
        // A lost connection reconnects by itself; a refused or ended one is opened again later, a little slower each time.
        if (stream.readyState !== EventSource.CLOSED || source !== stream) return;
        source = undefined;
        retry = setTimeout(() => {
            retry = undefined;
            syncLive();
        }, delay);
        delay = Math.min(delay * 2, LastDelay);
    });
}

function close(): void {
    source?.close();
    source = undefined;
    latest = undefined;
    if (retry !== undefined) clearTimeout(retry);
    retry = undefined;
}

// Installed once per document: a hidden tab closes its stream, a visible one opens a fresh one, and a swapped page binds again.
let installed = false;

export function installLive(): void {
    if (installed) return;
    installed = true;
    document.addEventListener("visibilitychange", syncLive);
    document.addEventListener("htmx:after:swap", () => queueMicrotask(syncLive));
    addEventListener("pagehide", close);
}
