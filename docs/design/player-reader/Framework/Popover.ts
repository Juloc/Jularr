// Native dismissal and focus; one viewport placement owner.
export function initializePopovers(root: HTMLElement): void {
    const panels = Array.from(root.querySelectorAll<HTMLElement>('[popover]'));
    const triggers = Array.from(root.querySelectorAll<HTMLElement>('[popovertarget]:not([popovertargetaction=hide])'));
    const place = (panel: HTMLElement): void => {
        if (!panel.matches(':popover-open')) return;
        const trigger = triggers.find(item => item.getAttribute('popovertarget') === panel.id);
        if (!trigger) return;
        const screen = panel.hasAttribute('data-popover-screen') && (innerWidth < 620 || innerHeight < 650);
        panel.toggleAttribute('data-popover-screen-active', screen);
        const anchor = trigger.getBoundingClientRect();
        const bounds = panel.getBoundingClientRect();
        const top = anchor.top >= bounds.height + 20 ? anchor.top - bounds.height - 8 : anchor.bottom + 8;
        panel.style.left = screen ? '0px' : `${Math.max(12, Math.min(innerWidth - bounds.width - 12, anchor.right - bounds.width))}px`;
        panel.style.top = screen ? '0px' : `${Math.max(12, Math.min(innerHeight - bounds.height - 12, top))}px`;
    };
    panels.forEach(panel => panel.addEventListener('toggle', () => place(panel)));
    const reposition = (): void => panels.forEach(place);
    window.addEventListener('resize', reposition);
    window.addEventListener('scroll', reposition, { passive: true });
}
