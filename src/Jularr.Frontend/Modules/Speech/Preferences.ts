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
    if (!synth) return () => {};
    function refresh(): void {
        for (const panel of Array.from(root.querySelectorAll<HTMLElement>('[data-speech-settings]'))) {
            const languages = panel.querySelector<HTMLSelectElement>('select[name=Language]')!;
            const language = languages.value;
            const available = new Set([language, ...Array.from(languages.options, option => option.value)]);
            for (const voice of synth.getVoices()) {
                try { available.add(Intl.getCanonicalLocales(voice.lang.replace(/_/g, '-'))[0]); } catch { /* Invalid provider metadata is not a language option. */ }
            }
            const names = typeof Intl.DisplayNames === 'function' ? new Intl.DisplayNames([document.documentElement.lang], { type: 'language' }) : undefined;
            languages.replaceChildren(...Array.from(available).sort().map(tag => {
                try { return new Option(names?.of(tag) ?? tag, tag); }
                catch { return new Option(tag, tag); }
            }));
            languages.value = language;
            const select = panel.querySelector<HTMLSelectElement>('select[name=VoiceId]')!;
            const provider = panel.querySelector<HTMLInputElement>('[name=ProviderTypeId]:checked, input[type=hidden][name=ProviderTypeId]')
                ?? panel.querySelector<HTMLSelectElement>('select[name=ProviderTypeId]');
            const neural = provider?.value === '2';
            const current = select.value;
            const voices = synth.getVoices().filter(voice => voice.lang.toLowerCase().split(/[-_]/)[0] === language.toLowerCase().split('-')[0])
                .sort((a, b) => a.name.localeCompare(b.name) || a.voiceURI.localeCompare(b.voiceURI));
            const options = [new Option(panel.dataset.speechDefaultLabel!, '')];
            if (current && (neural || !voices.some(voice => voice.voiceURI === current))) options.push(new Option(panel.dataset.speechMissingLabel! + ': ' + current, current));
            if (!neural) for (const voice of voices) options.push(new Option(voice.name + ' · ' + voice.lang, voice.voiceURI));
            select.replaceChildren(...options);
            select.value = current;
            panel.querySelector<HTMLElement>('[data-speech-voices-status]')!.hidden = !neural && voices.length > 0;
        }
    }
    const changed = (event: Event) => {
        const input = event.target as HTMLInputElement;
        if (!(input instanceof Element) || !input.closest('[data-speech-settings]')) return;
        if (input.name === 'Language' && input.closest('[data-speech-settings]')) {
            const panel = input.closest<HTMLElement>('[data-speech-settings]')!;
            const preferences = JSON.parse(panel.dataset.speechPreferences!) as SpeechPreferences;
            const choice = preferenceFor(preferences, input.value);
            const provider = panel.querySelector<HTMLSelectElement>('select[name=ProviderTypeId]')!;
            const options = [new Option(panel.dataset.speechDeviceLabel!, '1')];
            if (choice?.providerTypeId === 2) options.push(new Option(panel.dataset.speechNeuralLabel!, '2'));
            provider.replaceChildren(...options);
            provider.value = String(choice?.providerTypeId ?? 1);
            panel.querySelector<HTMLInputElement>('[name=ModelId]')!.value = choice?.modelId ?? '';
            const voice = panel.querySelector<HTMLSelectElement>('select[name=VoiceId]')!;
            voice.replaceChildren(new Option(panel.dataset.speechDefaultLabel!, ''), ...(choice?.voiceId ? [new Option(choice.voiceId, choice.voiceId)] : []));
            voice.value = choice?.voiceId ?? '';
            refresh();
            return;
        }
        if (input.name !== 'ProviderTypeId' || input.value !== '1') return;
        const panel = input.closest<HTMLElement>('[data-speech-settings]')!;
        panel.querySelector<HTMLInputElement>('[name=ModelId]')!.value = '';
        panel.querySelector<HTMLSelectElement>('[name=VoiceId]')!.value = '';
        refresh();
    };
    root.addEventListener('change', changed);
    synth.addEventListener('voiceschanged', refresh);
    refresh();
    const dispose = () => { root.removeEventListener('change', changed); synth.removeEventListener('voiceschanged', refresh); settingsRoots.delete(root); };
    settingsRoots.set(root, { refresh, dispose });
    return dispose;
}
