// The longer description expands with click, Enter or Space; one delegated listener serves every page.
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
    if ((event.key !== 'Enter' && event.key !== ' ') || !(event.target instanceof Element)) return;
    const summary = event.target.closest<HTMLElement>('[data-hero-summary]');
    if (summary && event.target === summary) {
        event.preventDefault();
        toggle(summary);
    }
});
