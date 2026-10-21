// The summary line of a title's hero opens on a click or Enter and closes on the next; one document-level listener serves every page.
function toggle(summary: HTMLElement): void {
    const open = summary.classList.toggle('media-hero__overview--open');
    summary.setAttribute('aria-expanded', String(open));
}

document.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;
    const summary = event.target.closest<HTMLElement>('[data-hero-summary]');
    if (summary) toggle(summary);
});

document.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || !(event.target instanceof Element)) return;
    const summary = event.target.closest<HTMLElement>('[data-hero-summary]');
    if (summary) toggle(summary);
});
