document.querySelector('.page-screen')?.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;
    const bookmark = event.target.closest<HTMLButtonElement>('[data-design-bookmark]');
    if (bookmark) bookmark.setAttribute('aria-pressed', String(bookmark.getAttribute('aria-pressed') !== 'true'));
});

export {};
