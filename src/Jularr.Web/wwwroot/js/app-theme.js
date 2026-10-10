(() => {
    const root = document.documentElement;
    const themeColor = document.querySelector('meta[data-app-theme-color]');

    const updateThemeColor = () => {
        if (!themeColor) return;
        const value = getComputedStyle(root)
            .getPropertyValue('--browser-theme-color')
            .trim();
        if (value) themeColor.setAttribute('content', value);
    };

    const apply = (mode) => {
        root.dataset.appTheme = mode;
        updateThemeColor();
        window.dispatchEvent(new CustomEvent('jularr:themechange', { detail: { mode } }));
    };

    // Appearance settings update the browser palette immediately while persisting through their canonical handler.
    window.JularrTheme = Object.freeze({ apply });

    const systemTheme = window.matchMedia('(prefers-color-scheme: dark)');
    systemTheme.addEventListener?.('change', () => {
        if (root.dataset.appTheme === 'system') updateThemeColor();
    });

    updateThemeColor();
})();
