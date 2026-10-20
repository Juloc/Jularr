import { DeviceSession, type SpeechItem, type SpeechState } from '../Speech/DeviceSession';
import { deviceVoice, initializeSpeechSettings, preferenceFor, type SpeechPreferences } from '../Speech/Preferences';

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
    if (!synth || typeof SpeechSynthesisUtterance === 'undefined' || !reader.dataset.speechPreferences) return;
    const preferences = JSON.parse(reader.dataset.speechPreferences) as SpeechPreferences;
    let manualUntil = 0;
    let preparing = 0;
    const highlights = (CSS as unknown as { highlights?: Map<string, unknown> }).highlights;
    const HighlightConstructor = (globalThis as unknown as { Highlight?: new (...ranges: Range[]) => unknown }).Highlight;
    let activeWord: Range | undefined;

    const clear = () => { activeWord = undefined; highlights?.delete('reader-speech-word'); };
    const changed = (state: SpeechState) => {
        reader.dataset.speechState = state;
        transport.hidden = state === 'idle';
        toggle.setAttribute('aria-pressed', String(state === 'speaking' || state === 'paused'));
        pause.setAttribute('aria-pressed', String(state === 'paused'));
        pause.setAttribute('aria-label', state === 'paused' ? reader.dataset.speechResumeLabel! : reader.dataset.speechPauseLabel!);
        status.textContent = state === 'failed' ? reader.dataset.speechFailedLabel! : state === 'paused' ? reader.dataset.speechPausedLabel!
            : state === 'speaking' ? reader.dataset.speechSpeakingLabel! : '';
        if (state === 'idle' || state === 'failed') clear();
    };
    const paintWord = () => {
        if (!activeWord || !highlights || !HighlightConstructor || reader.dataset.bookState && reader.dataset.bookState !== 'idle') return;
        const ranges = [activeWord];
        const block = activeWord.startContainer.parentElement?.closest<HTMLElement>('[data-b]');
        if (block && block.contains(activeWord.endContainer)) {
            const before = document.createRange(); before.selectNodeContents(block); before.setEnd(activeWord.startContainer, activeWord.startOffset);
            const from = before.toString().length, to = from + activeWord.toString().length;
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
        highlights.set('reader-speech-word', new HighlightConstructor(...ranges));
    };
    const session = new DeviceSession(synth, text => new SpeechSynthesisUtterance(text), changed, (item, offset, length) => {
        const range = speechRange(item.range, offset, length);
        if (!range) return;
        activeWord = range;
        const box = range.getClientRects()[0], bounds = content.getBoundingClientRect();
        if (box && Date.now() >= manualUntil && (box.left < bounds.left - 1 || box.right > bounds.right + 1 || box.top < bounds.top || box.bottom > bounds.bottom)) reveal(range);
        paintWord();
    });

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

    const stop = () => { preparing++; session.stop(); };
    toggle.disabled = false;
    toggle.addEventListener('click', async () => {
        if (session.state === 'speaking') { session.pause(); return; }
        if (session.state === 'paused') { session.resume(); return; }
        const version = ++preparing;
        if (reader.dataset.translationState === 'loading') {
            changed('failed'); status.textContent = reader.dataset.speechTranslationLabel!; return;
        }
        const chosen = reader.dataset.readingLanguage;
        const language = chosen && chosen !== 'original' && reader.dataset.translationState !== 'failed' ? chosen : reader.dataset.speechOriginalLanguage!;
        const preference = preferenceFor(preferences, language);
        if (preference?.providerTypeId === 2) { changed('failed'); status.textContent = reader.dataset.speechUnavailableLabel!; return; }
        if (!synth.getVoices().length) await new Promise<void>(resolve => {
            const ready = () => { synth.removeEventListener('voiceschanged', ready); clearTimeout(timer); resolve(); };
            const timer = window.setTimeout(ready, 1500);
            synth.addEventListener('voiceschanged', ready, { once: true });
        });
        if (version !== preparing) return;
        session.start(items(), { rate: preferences.rate, pitch: preferences.pitch, volume: preferences.volume, language,
            voice: deviceVoice(synth.getVoices(), language, preference?.voiceId ?? null) });
    });
    pause.addEventListener('click', () => session.state === 'paused' ? session.resume() : session.pause());
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
    document.addEventListener('visibilitychange', () => { if (document.hidden) { preparing++; session.pause(); } });
    window.addEventListener('pagehide', stop);
    changed('idle');
}
