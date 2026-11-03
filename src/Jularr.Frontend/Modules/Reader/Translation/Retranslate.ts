import { modelName, type RetranslationScope } from './translation';

export interface Retranslator {
    retranslate(model: string, scope: RetranslationScope, place: HTMLElement | undefined, progress: (done: number, total: number) => void): Promise<boolean>;
    modelAt(place: HTMLElement | undefined): string | null | undefined;
    showsServer(): boolean;
}

// Translating again with another model, in the language panel: the models a machine has now (asked once, when the panel first opens while the
// server's translation shows), then the paragraph at the top of the page, this chapter or the whole book. The line below names the model the
// paragraph's text is from, then how far the re-translation is.
export function initializeRetranslation(reader: HTMLElement, translation: Retranslator, place: () => HTMLElement | undefined): void {
    const area = reader.querySelector<HTMLElement>('[data-role=retranslate]');
    const panel = area?.closest<HTMLElement>('[data-panel]');
    if (!area || !panel) return;
    const list = area.querySelector<HTMLElement>('[data-role=retranslate-models]')!;
    const status = area.querySelector<HTMLElement>('[data-role=retranslate-status]')!;
    const actions = Array.from(area.querySelectorAll<HTMLButtonElement>('[data-retranslate]'));
    const own = () => reader.dataset.localModel ?? '';
    const named = (template: string | undefined, value: string) => (template ?? '').replace('{0}', value);
    let models: string[] | undefined;
    let loading: Promise<void> | undefined;
    let chosen: string | undefined;
    let running = false;

    function render() {
        list.replaceChildren(...(models ?? []).map(id => {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'button';
            button.textContent = modelName(id);
            button.dataset.retranslateModel = id;
            button.setAttribute('aria-pressed', String(id === chosen));
            button.disabled = running;
            button.addEventListener('click', () => { chosen = id; render(); });
            return button;
        }));
        actions.forEach(action => action.disabled = running || !chosen);
    }

    function paragraph() {
        if (running) return;
        const model = translation.modelAt(place());
        status.textContent = model ? named(area!.dataset.paragraphLabel, modelName(model)) : '';
    }

    async function load() {
        try {
            const response = await fetch(area!.dataset.modelsUrl!, { credentials: 'same-origin' });
            models = response.ok ? (await response.json() as { models: string[] }).models : [];
        } catch {
            models = [];
        }
        if (own() && !models.includes(own())) models.unshift(own());
        chosen = models.find(model => model !== own()) ?? models[0];
    }

    // Only while the server's translation shows, and only with a model besides the one that translates the book.
    async function refresh() {
        if (panel!.hidden || !translation.showsServer()) {
            if (!running) area!.hidden = true;
            return;
        }
        await (loading ??= load());
        area!.hidden = !models!.some(model => model !== own());
        render();
        paragraph();
    }

    new MutationObserver(() => void refresh()).observe(panel, { attributes: true, attributeFilter: ['hidden'] });
    new MutationObserver(() => void refresh()).observe(reader, { attributes: true, attributeFilter: ['data-translation-state', 'data-reading-language'] });

    actions.forEach(action => action.addEventListener('click', async () => {
        if (running || !chosen) return;
        const scope = action.dataset.retranslate as RetranslationScope;
        const model = chosen;
        running = true;
        render();
        const done = await translation.retranslate(model, scope, place(),
            (count, total) => { status.textContent = `${reader.dataset.translatingLabel ?? ''} ${Math.round(count * 100 / Math.max(1, total))} %`; });
        running = false;
        render();
        status.textContent = !done ? area.dataset.failedLabel ?? '' : scope === 'book' ? area.dataset.bookLabel ?? '' : named(area.dataset.doneLabel, modelName(model));
    }));
}
