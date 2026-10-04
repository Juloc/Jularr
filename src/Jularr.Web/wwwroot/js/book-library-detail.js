// Book detail page: destructive forms (Redo translation, Remove) ask for confirmation with the server-localized text
// of their data-confirm attribute before they submit.
(() => {
    document.addEventListener("submit", (event) => {
        const message = event.target instanceof HTMLFormElement ? event.target.dataset.confirm : null;
        if (message && !window.confirm(message)) {
            event.preventDefault();
        }
    });
})();
