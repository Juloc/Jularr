// The thumbs of a size range keep their order (minimum, preferred, maximum); the numbers above follow them and the top of the track reads as no limit.
export function clampThumb(values: number[], moved: number): number[] {
    const result = [...values];
    const lower = moved > 0 ? result[moved - 1] : -Infinity;
    const upper = moved < result.length - 1 ? result[moved + 1] : Infinity;
    result[moved] = Math.min(Math.max(result[moved], lower), upper);
    return result;
}

export function sizeText(value: number, limit: number, none: string, locale?: string): string {
    return value >= limit ? none : new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(value);
}

// The thumb on top where two meet: in the upper half the lower thumb, so a preferred size at the end of the track can still be pulled back; below the half the upper one.
export function thumbLayers(values: number[], limit: number): number[] {
    return values.map((value, index) => value > limit / 2 ? values.length - index : index + 1);
}

export function updateSizeRange(range: HTMLElement, moved?: HTMLInputElement): void {
    const thumbs = [...range.querySelectorAll<HTMLInputElement>('input[data-size-thumb]')];
    const limit = Number(range.dataset.sizeLimit);
    const none = range.dataset.sizeNone ?? '';
    const index = moved ? thumbs.indexOf(moved) : -1;
    if (index >= 0) {
        const values = clampThumb(thumbs.map(thumb => thumb.valueAsNumber), index);
        if (values[index] !== moved!.valueAsNumber) moved!.valueAsNumber = values[index];
    }

    const layers = thumbLayers(thumbs.map(thumb => thumb.valueAsNumber), limit);
    thumbs.forEach((thumb, index) => { thumb.style.zIndex = String(layers[index]); });
    for (const thumb of thumbs) {
        const output = range.querySelector<HTMLOutputElement>(`output[data-size-output="${thumb.dataset.sizeThumb}"]`);
        if (output) output.value = sizeText(thumb.valueAsNumber, limit, none, document.documentElement.lang || undefined);
    }

    // The span between the lowest and the highest thumb is tinted.
    const first = thumbs[0]?.valueAsNumber ?? 0, last = thumbs[thumbs.length - 1]?.valueAsNumber ?? limit;
    range.style.setProperty('--size-from', `${(first / limit) * 100}%`);
    range.style.setProperty('--size-to', `${(last / limit) * 100}%`);
}
