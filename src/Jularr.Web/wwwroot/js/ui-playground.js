(() => {
    const preview = document.querySelector('[data-ui-playground]');
    if (!preview) return;
    preview.querySelector('form').addEventListener('submit', event => event.preventDefault());
    preview.addEventListener('ui:select-commit', event => {
        preview.querySelector('[data-ui-playground-output]').textContent = event.detail.values.join(', ') || 'All states';
    });
    window.JularrTableSelection.init(preview.querySelector('[data-ui-playground-table]'), selected => {
        preview.querySelector('[data-ui-playground-selection]').textContent = `${selected.length} selected`;
    });
    document.querySelector('[data-ui-playground-editor]').addEventListener('click', () => {
        preview.classList.add('ui-split-editing');
        preview.querySelector('.ui-split-editor button').focus();
    });
    preview.querySelector('[data-ui-playground-back]').addEventListener('click', () => {
        preview.classList.remove('ui-split-editing');
        preview.querySelector('[data-ui-select-trigger]').focus();
    });
})();
