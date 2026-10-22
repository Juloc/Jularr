// Official prerecorded samples only. No book text, model weights or synthesis runtime
// are sent or loaded by this page. One sample can play, and only after a click.
let active: { audio: HTMLAudioElement; button: HTMLButtonElement } | undefined;

function stop(): void {
    if (!active) return;
    const { audio, button } = active;
    active = undefined;
    audio.pause();
    audio.removeAttribute('src');
    audio.load();
    button.setAttribute('aria-pressed', 'false');
    const label = button.querySelector('[data-sample-label]');
    if (label) label.textContent = button.dataset.play ?? '';
}

document.addEventListener('click', event => {
    const button = event.target instanceof Element ? event.target.closest<HTMLButtonElement>('[data-speech-sample]') : null;
    if (!button) return;
    const wasActive = active?.button === button;
    stop();
    if (wasActive) return;
    const audio = new Audio();
    audio.preload = 'none';
    const status = button.parentElement?.querySelector<HTMLElement>('[data-sample-status]');
    if (status) status.textContent = '';
    active = { audio, button };
    button.setAttribute('aria-pressed', 'true');
    const label = button.querySelector('[data-sample-label]');
    if (label) label.textContent = button.dataset.stop ?? '';
    const failed = () => {
        if (active?.audio !== audio) return;
        stop();
        if (status) status.textContent = button.dataset.failed ?? '';
    };
    audio.addEventListener('error', failed, { once: true });
    audio.addEventListener('ended', () => { if (active?.audio === audio) stop(); }, { once: true });
    audio.src = button.dataset.speechSample!;
    void audio.play().catch(failed);
});

document.addEventListener('htmx:before:swap', stop);
document.addEventListener('visibilitychange', () => { if (document.hidden) stop(); });
window.addEventListener('pagehide', stop);
