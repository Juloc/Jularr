// Native range input owns keyboard, pointer and submitted value; this only shows its value.
export function updateRangeValue(input: HTMLInputElement): void {
    const minimum = Number(input.min || 0), maximum = Number(input.max || 100);
    const fraction = maximum > minimum && Number.isFinite(input.valueAsNumber) ? (input.valueAsNumber - minimum) / (maximum - minimum) : 0;
    input.style.setProperty('--range-progress', `${Math.min(100, Math.max(0, fraction * 100))}%`);
    const output = input.parentElement?.querySelector('output');
    if (output && Number.isFinite(input.valueAsNumber)) output.value = new Intl.NumberFormat(document.documentElement.lang || undefined,
        { maximumFractionDigits: Number(input.dataset.rangePrecision ?? 20) }).format(input.valueAsNumber);
}

export function initializeRanges(root: HTMLElement): void {
    if (!root.dataset.rangesReady) {
        root.dataset.rangesReady = 'true';
        root.addEventListener('input', event => {
            if (event.target instanceof HTMLInputElement && event.target.matches('.ui-range > input[type=range]')) updateRangeValue(event.target);
        });
        root.addEventListener('reset', () => queueMicrotask(() => initializeRanges(root)));
    }
    root.querySelectorAll<HTMLInputElement>('.ui-range > input[type=range]').forEach(updateRangeValue);
}
