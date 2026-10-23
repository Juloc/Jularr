import { installedModels, removeModel, type InstalledModel, type SpeechModel } from './ModelStore';
import noticeUrl from './Runtime/NOTICE.generated.txt?url';

export interface SpeechVoiceChoice { language: string; providerTypeId: number; modelId: string | null; voiceId: string | null }
export interface SpeechPreferences { rate: number; pitch: number; volume: number; version: number; voices: SpeechVoiceChoice[] }

export function preferenceFor(preferences: SpeechPreferences, language: string): SpeechVoiceChoice | undefined {
    const tag = language.toLowerCase();
    return preferences.voices.find(voice => voice.language.toLowerCase() === tag)
        ?? preferences.voices.find(voice => voice.language.toLowerCase() === tag.split('-')[0])
        ?? preferences.voices.find(voice => voice.language.toLowerCase().split('-')[0] === tag.split('-')[0]);
}

export function deviceVoice(voices: SpeechSynthesisVoice[], language: string, preferred: string | null): SpeechSynthesisVoice | undefined {
    const tag = language.toLowerCase(), base = tag.split('-')[0];
    const compatible = voices.filter(voice => voice.lang.toLowerCase().split(/[-_]/)[0] === base);
    const ordered = compatible.sort((a, b) => Number(b.lang.toLowerCase() === tag) - Number(a.lang.toLowerCase() === tag)
        || Number(b.default) - Number(a.default) || a.voiceURI.localeCompare(b.voiceURI));
    return ordered.find(voice => voice.voiceURI === preferred) ?? ordered[0];
}

const settingsRoots = new WeakMap<HTMLElement, { refresh: () => void; dispose: () => void }>();

export function initializeSpeechSettings(root: HTMLElement): () => void {
    const existing = settingsRoots.get(root);
    if (existing) { existing.refresh(); return existing.dispose; }
    const synth = globalThis.speechSynthesis;
    let installed: InstalledModel[] = [];
    let disposed = false;
    let storageFailed = false;
    let operation: AbortController | undefined;
    let result: 'failed' | 'removed' | undefined;
    const provider = (panel: HTMLElement) => panel.querySelector<HTMLInputElement>('[name=ProviderTypeId]:checked, input[type=hidden][name=ProviderTypeId]')?.value
        ?? panel.querySelector<HTMLSelectElement>('select[name=ProviderTypeId]')?.value ?? '1';
    const setProvider = (panel: HTMLElement, value: string) => panel.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[name=ProviderTypeId]').forEach(input => {
        if (input instanceof HTMLInputElement && input.type === 'radio') input.checked = input.value === value;
        else input.value = value;
    });
    const catalog = (panel: HTMLElement) => JSON.parse(panel.dataset.speechModels ?? '[]') as SpeechModel[];
    const compatible = (model: SpeechModel, language: string) => model.language.toLowerCase().split('-')[0] === language.toLowerCase().split('-')[0];
    function refresh(): void {
        for (const panel of Array.from(root.querySelectorAll<HTMLElement>('[data-speech-settings]'))) {
            const notices = panel.querySelector<HTMLAnchorElement>('[data-speech-runtime-notices]');
            if (notices) notices.href = noticeUrl;
            const languages = panel.querySelector<HTMLSelectElement>('select[name=Language]')!;
            const language = languages.value;
            const available = new Set([language, ...Array.from(languages.options, option => option.value)]);
            for (const model of [...catalog(panel), ...installed]) available.add(model.language);
            for (const voice of synth?.getVoices() ?? []) {
                try { available.add(Intl.getCanonicalLocales(voice.lang.replace(/_/g, '-'))[0]); } catch { /* Invalid provider metadata is not a language option. */ }
            }
            const names = typeof Intl.DisplayNames === 'function' ? new Intl.DisplayNames([document.documentElement.lang], { type: 'language' }) : undefined;
            languages.replaceChildren(...Array.from(available).sort().map(tag => {
                try { return new Option(names?.of(tag) ?? tag, tag); }
                catch { return new Option(tag, tag); }
            }));
            languages.value = language;
            const select = panel.querySelector<HTMLSelectElement>('select[name=VoiceId]')!;
            const neural = provider(panel) === '2';
            const models = catalog(panel).filter(model => compatible(model, language));
            const cached = installed.filter(model => compatible(model, language));
            const choices = [...models, ...cached.filter(model => !models.some(other => other.id === model.id))];
            const modelInput = panel.querySelector<HTMLSelectElement>('[name=ModelId]')!;
            const modelId = neural ? modelInput.value : '';
            const modelOptions = [new Option(panel.dataset.speechModelChoose!, '')];
            if (modelId && !choices.some(model => model.id === modelId)) modelOptions.push(new Option(panel.dataset.speechMissingLabel! + ': ' + modelId, modelId));
            for (const model of choices) modelOptions.push(new Option(model.name + ' · ' + model.language, model.id));
            modelInput.replaceChildren(...modelOptions); modelInput.value = modelId; modelInput.inert = !neural;
            const selected = choices.find(model => model.id === modelId);
            const local = cached.find(model => model.id === modelId);
            const current = select.value;
            const voices = (synth?.getVoices() ?? []).filter(voice => voice.lang.toLowerCase().split(/[-_]/)[0] === language.toLowerCase().split('-')[0])
                .sort((a, b) => a.name.localeCompare(b.name) || a.voiceURI.localeCompare(b.voiceURI));
            const options = [new Option(neural ? panel.dataset.speechVoiceChoose! : panel.dataset.speechDefaultLabel!, '')];
            if (neural && selected) options.push(new Option(selected.name, selected.voice));
            else if (current && !voices.some(voice => voice.voiceURI === current)) options.push(new Option(panel.dataset.speechMissingLabel! + ': ' + current, current));
            if (!neural) for (const voice of voices) options.push(new Option(voice.name + ' · ' + voice.lang, voice.voiceURI));
            select.replaceChildren(...options);
            select.value = neural && selected ? selected.voice : current;
            panel.querySelector<HTMLElement>('[data-speech-voices-status]')!.hidden = neural || voices.length > 0;
            const pitch = panel.querySelector<HTMLInputElement>('[name=Pitch]')!;
            // Inert prevents interaction while retaining the bound value in native and enhanced form submissions.
            pitch.inert = neural; pitch.setAttribute('aria-disabled', String(neural));
            panel.querySelector<HTMLElement>('[data-speech-pitch-note]')!.hidden = !neural;
            const install = panel.querySelector<HTMLButtonElement>('[data-speech-install]')!;
            const remove = panel.querySelector<HTMLButtonElement>('[data-speech-remove]')!;
            install.hidden = remove.hidden = !neural;
            install.disabled = !!operation || storageFailed || !models.some(model => model.id === modelId && model.sha256 !== local?.sha256);
            remove.disabled = !!operation || !local;
            const status = panel.querySelector<HTMLElement>('[data-speech-model-status]')!;
            status.hidden = !neural;
            status.textContent = storageFailed ? panel.dataset.speechStorageFailed! : operation ? panel.dataset.speechModelBusy!
                : result === 'failed' ? panel.dataset.speechModelFailed! : result === 'removed' ? panel.dataset.speechModelRemoved!
                    : local ? panel.dataset.speechModelInstalled! + ' · ' + local.version : panel.dataset.speechModelMissing!;
            const license = panel.querySelector<HTMLAnchorElement>('[data-speech-model-license]');
            if (license) {
                license.hidden = !neural || !selected;
                if (selected && /^https:\/\//.test(selected.licenseUrl)) { license.href = selected.licenseUrl; license.textContent = selected.license; }
            }
        }
    }
    const changed = (event: Event) => {
        const input = event.target as HTMLInputElement;
        if (!(input instanceof Element) || !input.closest('[data-speech-settings]')) return;
        if (input.name === 'Language' && input.closest('[data-speech-settings]')) {
            const panel = input.closest<HTMLElement>('[data-speech-settings]')!;
            const preferences = JSON.parse(panel.dataset.speechPreferences!) as SpeechPreferences;
            const choice = preferenceFor(preferences, input.value);
            setProvider(panel, String(choice?.providerTypeId ?? 1));
            const model = panel.querySelector<HTMLSelectElement>('[name=ModelId]')!;
            model.replaceChildren(new Option(choice?.modelId ?? '', choice?.modelId ?? '')); model.value = choice?.modelId ?? '';
            const voice = panel.querySelector<HTMLSelectElement>('select[name=VoiceId]')!;
            voice.replaceChildren(new Option(panel.dataset.speechDefaultLabel!, ''), ...(choice?.voiceId ? [new Option(choice.voiceId, choice.voiceId)] : []));
            voice.value = choice?.voiceId ?? '';
            refresh();
            return;
        }
        const panel = input.closest<HTMLElement>('[data-speech-settings]')!;
        result = undefined;
        if (input.name === 'ProviderTypeId') {
            panel.querySelector<HTMLSelectElement>('[name=ModelId]')!.value = '';
            panel.querySelector<HTMLSelectElement>('[name=VoiceId]')!.value = '';
        }
        refresh();
    };
    const clicked = async (event: Event) => {
        const button = (event.target as Element).closest<HTMLButtonElement>('[data-speech-install], [data-speech-remove]');
        const panel = button?.closest<HTMLElement>('[data-speech-settings]');
        if (!button || !panel || button.disabled || operation) return;
        const id = panel.querySelector<HTMLSelectElement>('[name=ModelId]')!.value;
        operation = new AbortController(); result = undefined; refresh();
        try {
            if (button.hasAttribute('data-speech-install')) {
                const model = catalog(panel).find(model => model.id === id);
                if (!model) throw new Error('Speech model is unavailable.');
                const { installModel } = await import('./ModelInstall');
                operation.signal.throwIfAborted();
                await installModel(model, operation.signal);
            } else {
                await removeModel(id); result = 'removed';
                window.dispatchEvent(new CustomEvent('speech-model-removed', { detail: id }));
            }
            installed = await installedModels();
        } catch { if (!disposed) result = 'failed'; }
        finally { operation = undefined; if (!disposed) refresh(); }
    };
    const leaving = () => operation?.abort();
    root.addEventListener('change', changed);
    root.addEventListener('click', clicked);
    window.addEventListener('pagehide', leaving);
    synth?.addEventListener('voiceschanged', refresh);
    refresh();
    void installedModels().then(models => { installed = models; if (!disposed) refresh(); }).catch(() => { storageFailed = true; if (!disposed) refresh(); });
    const dispose = () => {
        disposed = true; leaving(); root.removeEventListener('change', changed); root.removeEventListener('click', clicked);
        window.removeEventListener('pagehide', leaving); synth?.removeEventListener('voiceschanged', refresh); settingsRoots.delete(root);
    };
    settingsRoots.set(root, { refresh, dispose });
    return dispose;
}
