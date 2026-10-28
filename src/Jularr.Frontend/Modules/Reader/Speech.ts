import { DeviceSession, type SpeechItem, type SpeechState } from '../Speech/DeviceSession';
import { deviceVoice, initializeSpeechSettings, preferenceFor, type SpeechPreferences } from '../Speech/Preferences';
import { installedModels } from '../Speech/ModelStore';
import type { NeuralSession } from '../Speech/NeuralSession';
import { followDelta, minutesLeft, sentenceAround, timerMinutes } from './SpeechFollow';
import { readerLifetime } from './Lifetime';
import { awake, focus } from '../Playback/MediaFocus';

// Text coordinates stay on canonical content. Highlights never wrap or mutate its nodes.
export function speechRange(source: Range, start: number, length: number): Range | undefined {
    const result = document.createRange();
    const root = source.commonAncestorContainer;
    const texts: Text[] = [];
    if (root.nodeType === Node.TEXT_NODE) texts.push(root as Text);
    else {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        while (walker.nextNode()) if (source.intersectsNode(walker.currentNode)) texts.push(walker.currentNode as Text);
    }
    let consumed = 0, started = false;
    for (const text of texts) {
        const from = source.startContainer === text ? source.startOffset : 0;
        const to = source.endContainer === text ? source.endOffset : text.length;
        const count = to - from;
        if (!started && start < consumed + count) { result.setStart(text, from + start - consumed); started = true; }
        if (started && start + length <= consumed + count) { result.setEnd(text, from + start + length - consumed); return result; }
        consumed += count;
    }
    return undefined;
}

export function initializeReadAloud(reader: HTMLElement, content: HTMLElement, reveal: (range: Range) => void, first: () => HTMLElement | undefined): void {
    initializeSpeechSettings(document.documentElement);
    const toggle = reader.querySelector<HTMLButtonElement>('[data-speech-toggle]')!;
    const transport = reader.querySelector<HTMLElement>('[data-speech-transport]')!;
    const pause = reader.querySelector<HTMLButtonElement>('[data-speech-pause]')!;
    const status = reader.querySelector<HTMLElement>('[data-speech-status]')!;
    const synth = globalThis.speechSynthesis;
    if (!reader.dataset.speechPreferences) return;
    const preferences = JSON.parse(reader.dataset.speechPreferences) as SpeechPreferences;
    let manualUntil = 0;
    let preparing = 0;
    let session: DeviceSession | NeuralSession | undefined;
    let pendingContext: AudioContext | undefined;
    let neuralModel: string | undefined;
    const highlights = (CSS as unknown as { highlights?: Map<string, unknown> }).highlights;
    const HighlightConstructor = (globalThis as unknown as { Highlight?: new (...ranges: Range[]) => unknown }).Highlight;
    let activeWord: Range | undefined, activeSentence: Range | undefined;

    const clear = () => { activeWord = activeSentence = undefined; highlights?.delete('reader-speech-word'); highlights?.delete('reader-speech-sentence'); };
    // The sleep timer: a chosen number of minutes after which reading aloud stops by itself.
    const timerLeft = reader.querySelector<HTMLElement>('[data-speech-timer-left]');
    const timerChoice = reader.querySelector<HTMLSelectElement>('[data-panel=timer] select');
    let timerEnds = 0, timerStop = 0, timerTick = 0;
    const showTimer = () => { if (timerLeft) timerLeft.textContent = timerEnds ? reader.dataset.timerLeftLabel!.replace('{0}', String(minutesLeft(timerEnds, Date.now()))) : ''; };
    const setTimer = (minutes: number) => {
        window.clearTimeout(timerStop); window.clearInterval(timerTick);
        timerEnds = minutes ? Date.now() + minutes * 60_000 : 0;
        timerStop = minutes ? window.setTimeout(() => { setTimer(0); if (timerChoice) timerChoice.value = '0'; stop(); }, minutes * 60_000) : 0;
        timerTick = minutes ? window.setInterval(showTimer, 10_000) : 0;
        showTimer();
    };
    timerChoice?.addEventListener('change', () => setTimer(timerMinutes(timerChoice.value)));
    const changed = (state: SpeechState) => {
        if (state === 'idle' || state === 'failed') { setTimer(0); if (timerChoice) timerChoice.value = '0'; }
        reader.dataset.speechState = state;
        // Reading aloud takes the focus from a playing video and holds the screen on; pausing or ending gives both up.
        if (state === 'speaking' || state === 'loading') { awake.hold('read-aloud'); focus.claim('read-aloud', () => session?.pause()); }
        else { awake.drop('read-aloud'); focus.release('read-aloud'); }
        transport.hidden = state === 'idle';
        toggle.setAttribute('aria-pressed', String(state === 'speaking' || state === 'paused' || state === 'loading'));
        pause.setAttribute('aria-pressed', String(state === 'paused'));
        pause.setAttribute('aria-label', state === 'paused' ? reader.dataset.speechResumeLabel! : reader.dataset.speechPauseLabel!);
        status.textContent = state === 'loading' ? reader.dataset.speechLoadingLabel! : state === 'failed' ? reader.dataset.speechFailedLabel! : state === 'paused' ? reader.dataset.speechPausedLabel!
            : state === 'speaking' ? reader.dataset.speechSpeakingLabel! : '';
        if (state === 'idle' || state === 'failed') clear();
    };
    // The range itself and, on the printed pages of the book, the runs that carry its text.
    const inked = (active: Range): Range[] => {
        const ranges = [active];
        const firstBlock = active.startContainer.parentElement?.closest<HTMLElement>('[data-b]');
        const blocks = firstBlock && firstBlock.contains(active.endContainer) ? [firstBlock]
            : Array.from(content.querySelectorAll<HTMLElement>('[data-b]')).filter(block => !block.querySelector('[data-b]') && active.intersectsNode(block));
        for (const block of blocks) {
            const local = document.createRange(); local.selectNodeContents(block);
            if (block.contains(active.startContainer)) local.setStart(active.startContainer, active.startOffset);
            if (block.contains(active.endContainer)) local.setEnd(active.endContainer, active.endOffset);
            const before = document.createRange(); before.selectNodeContents(block); before.setEnd(local.startContainer, local.startOffset);
            const from = before.toString().length, to = from + local.toString().length;
            const text = block.textContent ?? '';
            for (const ink of Array.from(reader.querySelectorAll<HTMLElement>(`[data-print-block="${block.dataset.b}"]`))) {
                const start = Number(ink.dataset.printStart), end = Number(ink.dataset.printEnd);
                if (end <= from || start >= to || !ink.firstChild) continue;
                const normalize = (value: string) => ink.dataset.printPreserved === 'true' ? value : value.replace(/\s+/g, ' ');
                const range = document.createRange();
                range.setStart(ink.firstChild, Math.min(ink.textContent!.length, normalize(text.slice(start, Math.max(start, from))).length));
                range.setEnd(ink.firstChild, Math.min(ink.textContent!.length, normalize(text.slice(start, Math.min(end, to))).length));
                ranges.push(range);
            }
        }
        return ranges;
    };
    // The spoken word is marked strongly, its sentence softly around it.
    const paintWord = () => {
        if (!activeWord || !highlights || !HighlightConstructor || reader.dataset.bookState && reader.dataset.bookState !== 'idle') return;
        if (activeSentence) highlights.set('reader-speech-sentence', new HighlightConstructor(...inked(activeSentence)));
        else highlights.delete('reader-speech-sentence');
        highlights.set('reader-speech-word', new HighlightConstructor(...inked(activeWord)));
    };
    const segmenter = typeof Intl.Segmenter === 'function' ? new Intl.Segmenter(document.documentElement.lang || undefined, { granularity: 'sentence' }) : undefined;
    const highlight = (item: import('../Speech/DeviceSession').SpeechItem, offset: number, length: number) => {
        const range = speechRange(item.range, offset, length);
        if (!range) return;
        activeWord = range;
        const around = segmenter ? sentenceAround(item.text, offset, text => segmenter.segment(text)) : undefined;
        activeSentence = around ? speechRange(item.range, around.start, around.length) : undefined;
        const box = range.getClientRects()[0], bounds = content.getBoundingClientRect();
        if (box && Date.now() >= manualUntil) {
            if (reader.dataset.mode === 'scroll') {
                // A scrolled text follows the voice, keeping its line a little above the middle.
                const delta = followDelta(box.top, bounds);
                if (delta) content.scrollBy({ top: delta, behavior: reader.dataset.animation === 'off' || matchMedia('(prefers-reduced-motion: reduce)').matches && reader.dataset.animation !== 'on' ? 'instant' : 'smooth' });
            } else if (box.left < bounds.left - 1 || box.right > bounds.right + 1 || box.top < bounds.top || box.bottom > bounds.bottom) reveal(range);
        }
        paintWord();
    };
    const device = synth && typeof SpeechSynthesisUtterance !== 'undefined'
        ? new DeviceSession(synth, text => new SpeechSynthesisUtterance(text), changed, highlight) : undefined;

    function* items(): Generator<SpeechItem> {
        const selection = getSelection();
        if (selection && !selection.isCollapsed && selection.rangeCount && content.contains(selection.getRangeAt(0).commonAncestorContainer)) {
            const range = selection.getRangeAt(0).cloneRange();
            selection.removeAllRanges();
            yield { text: range.toString(), range };
            return;
        }
        const blocks = Array.from(content.querySelectorAll<HTMLElement>('[data-b]')).filter(block => !block.querySelector('[data-b]'));
        const visible = first();
        const start = visible ? blocks.findIndex(block => block === visible || visible.contains(block) || block.contains(visible)) : 0;
        for (let index = Math.max(0, start); index < blocks.length; index++) {
            const range = document.createRange(); range.selectNodeContents(blocks[index]);
            yield { text: range.toString(), range };
        }
    }

    const stop = () => {
        preparing++; session?.stop(); neuralModel = undefined;
        if (pendingContext && pendingContext.state !== 'closed') void pendingContext.close().catch(() => {});
        pendingContext = undefined;
    };
    toggle.disabled = false;
    toggle.addEventListener('click', async () => {
        if (session?.state === 'speaking' || session?.state === 'loading') { session.pause(); return; }
        if (session?.state === 'paused') { session.resume(); return; }
        const version = ++preparing;
        if (reader.dataset.translationState === 'loading') {
            changed('failed'); status.textContent = reader.dataset.speechTranslationLabel!; return;
        }
        const chosen = reader.dataset.readingLanguage;
        const language = chosen && chosen !== 'original' && reader.dataset.translationState !== 'failed' ? chosen : reader.dataset.speechOriginalLanguage!;
        const preference = preferenceFor(preferences, language);
        if (preference?.providerTypeId === 2) {
            try {
                // Create/resume during the user's gesture, before asynchronous imports or cache reads.
                const context = pendingContext = new AudioContext();
                void context.resume().catch(() => {});
                const [{ NeuralSession }, models] = await Promise.all([import('../Speech/NeuralSession'), installedModels()]);
                if (version !== preparing) { if (context.state !== 'closed') void context.close(); return; }
                const model = models.find(model => model.id === preference.modelId && model.voice === preference.voiceId
                    && model.language.toLowerCase().split('-')[0] === language.toLowerCase().split('-')[0]);
                if (!model) { stop(); changed('failed'); status.textContent = reader.dataset.speechUnavailableLabel!; return; }
                session?.stop();
                session = new NeuralSession(changed, highlight);
                neuralModel = model.id; pendingContext = undefined;
                session.start(items(), { rate: preferences.rate, pitch: preferences.pitch, volume: preferences.volume, language }, model, context);
            } catch { if (version === preparing) { stop(); changed('failed'); } }
            return;
        }
        if (!device || !synth) { changed('failed'); return; }
        if (!synth.getVoices().length) await new Promise<void>(resolve => {
            const ready = () => { synth.removeEventListener('voiceschanged', ready); clearTimeout(timer); resolve(); };
            const timer = window.setTimeout(ready, 1500);
            synth.addEventListener('voiceschanged', ready, { once: true });
        });
        if (version !== preparing) return;
        session?.stop(); session = device; neuralModel = undefined;
        session.start(items(), { rate: preferences.rate, pitch: preferences.pitch, volume: preferences.volume, language,
            voice: deviceVoice(synth.getVoices(), language, preference?.voiceId ?? null) });
    });
    pause.addEventListener('click', () => session?.state === 'paused' ? session.resume() : session?.pause());
    reader.querySelector('[data-speech-stop]')!.addEventListener('click', stop);
    reader.querySelectorAll('[data-language-choice]').forEach(button => button.addEventListener('click', stop));
    const manual = () => { manualUntil = Date.now() + 3000; };
    reader.querySelector('.reader__canvas')!.addEventListener('wheel', manual, { passive: true });
    reader.querySelector('.reader__canvas')!.addEventListener('pointerdown', manual, { passive: true });
    reader.addEventListener('keydown', event => { if (['ArrowLeft', 'ArrowRight', 'PageDown', 'PageUp', 'Home', 'End'].includes(event.key)) manual(); });
    new MutationObserver(stop).observe(content, { childList: true, subtree: true, characterData: true });
    const prints = new MutationObserver(paintWord);
    prints.observe(reader.querySelector('.reader-book')!, { childList: true, subtree: true });
    prints.observe(reader, { attributes: true, attributeFilter: ['data-book-state'] });
    const signal = readerLifetime();
    document.addEventListener('visibilitychange', () => { if (document.hidden) { preparing++; session?.pause(); if (pendingContext) stop(); } }, { signal });
    window.addEventListener('speech-model-removed', event => { if ((event as CustomEvent<string>).detail === neuralModel) stop(); }, { signal });
    window.addEventListener('pagehide', stop, { signal });
    signal.addEventListener('abort', () => { setTimer(0); stop(); });
    changed('idle');
}
