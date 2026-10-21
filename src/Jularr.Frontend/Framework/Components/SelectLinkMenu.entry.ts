// One document-level listener: an outside click or Escape closes any open link menu or action menu.
const MENUS = 'details.select-link-menu[open], details.action-menu[open]';

function closeOutside(target: EventTarget | null): void {
    document.querySelectorAll<HTMLDetailsElement>(MENUS).forEach(menu => {
        if (!(target instanceof Node) || !menu.contains(target)) menu.open = false;
    });
}

document.addEventListener('click', event => closeOutside(event.target));
document.addEventListener('keydown', event => {
    if (event.key === 'Escape') closeOutside(null);
});
