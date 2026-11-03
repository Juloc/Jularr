import { modelName, type RetranslationScope } from './translation';
import { retranslatePassages } from './codex';

export interface Retranslator {
    retranslate(model: string, scope: RetranslationScope, place: HTMLElement | undefined, progress: (done: number, total: number) => void): Promise<boolean>;
    modelAt(place: HTMLElement | undefined): string | null | undefined;
    showsServer(): boolean;
}

// The Reader's one re-translation, in the language panel: the models a machine has now (asked once, when the panel first opens while the
// server's translation shows), then the paragraph at the top of the page, this chapter or the whole book. The line below names the model the
// paragraph's text is from, then how far the re-translation is. A model that translates whole passages (Codex) re-translates a chapter or the book
// chapter by chapter with progress and Stop (codex.ts); its paragraph goes block by block like every other model's.
export function initializeRetranslation(reader: HTMLElement, translation: Retranslator, place: () => HTMLElement | undefined): void {
    const area = reader.querySelector<HTMLElement>('[data-role=retranslate]');
    const panel = area?.closest<HTMLElement>('[data-panel]');
    if (!area || !panel) return;
    const list = area.querySelector<HTMLElement>('[data-role=retranslate-models]')!;
    const status = area.querySelector<HTMLElement>('[data-role=retranslate-status]')!;
    const actions = Array.from(area.querySelectorAll<HTMLButtonElement>('[data-retranslate]'));
    const stop = area.querySelector<HTMLButtonElement>('[data-retranslate-stop]');
    const own = () => reader.dataset.localModel ?? '';
    const named = (template: string | undefined, value: string) => (template ?? '').replace('{0}', value);
    // Passages: chapters go to the server in whole-passage requests.
    let models: { id: string; passages: boolean }[] | undefined;
    let loading: Promise<void> | undefined;
    let chosen: string | undefined;
    let running = false;
    let stopping = false;

    function render() {
        list.replaceChildren(...(models ?? []).map(({ id }) => {
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
            models = response.ok ? (await response.json() as { models: { id: string; passages: boolean }[] }).models : [];
        } catch {
            models = [];
        }
        if (own() && !models.some(model => model.id === own())) models.unshift({ id: own(), passages: false });
        chosen = (models.find(model => model.id !== own()) ?? models[0])?.id;
    }

    // Only while the server's translation shows, and only with a model besides the one that translates the book.
    async function refresh() {
        if (panel!.hidden || !translation.showsServer()) {
            if (!running) area!.hidden = true;
            return;
        }
        await (loading ??= load());
        area!.hidden = !models!.some(model => model.id !== own());
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
        stopping = false;
        render();
        if (scope !== 'block' && models?.find(item => item.id === model)?.passages) {
            if (stop) stop.hidden = false;
            await retranslatePassages(reader, area, model, scope, () => stopping, text => { status.textContent = text; });
            if (stop) stop.hidden = true;
            running = false;
            render();
            return;
        }
        const done = await translation.retranslate(model, scope, place(),
            (count, total) => { status.textContent = `${reader.dataset.translatingLabel ?? ''} ${Math.round(count * 100 / Math.max(1, total))} %`; });
        running = false;
        render();
        status.textContent = !done ? area.dataset.failedLabel ?? '' : scope === 'book' ? area.dataset.bookLabel ?? '' : named(area.dataset.doneLabel, modelName(model));
    }));
    stop?.addEventListener('click', () => { stopping = true; });
}
