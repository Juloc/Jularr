(() => {
    const place = (panel, trigger, bottomInset = 8, alignment = 'start') => {
        const bounds = trigger.getBoundingClientRect();
        const width = document.documentElement.clientWidth;
        const height = document.documentElement.clientHeight;
        const margin = 8;
        const gap = 6;
        panel.style.maxWidth = `${Math.max(0, width - margin * 2)}px`;
        panel.style.minWidth = '';
        panel.style.minWidth = `${Math.max(0, Math.min(Math.max(bounds.width, panel.offsetWidth), width - margin * 2))}px`;
        panel.style.maxHeight = '';
        const navigation = document.querySelector?.('.mobile-nav')?.getBoundingClientRect();
        const bottom = navigation?.height > 0 ? Math.min(height - bottomInset, navigation.top - margin) : height - bottomInset;
        const below = bottom - bounds.bottom - gap;
        const above = bounds.top - margin - gap;
        const useBelow = below >= panel.offsetHeight || below >= above;
        panel.style.maxHeight = `${Math.max(0, useBelow ? below : above)}px`;
        const left = alignment === 'end' ? bounds.right - panel.offsetWidth : bounds.left;
        panel.style.left = `${Math.max(margin, Math.min(left, width - panel.offsetWidth - margin))}px`;
        panel.style.top = `${useBelow ? bounds.bottom + gap : Math.max(margin, bounds.top - panel.offsetHeight - gap)}px`;
    };
    window.JularrPopover = { place };
})();
