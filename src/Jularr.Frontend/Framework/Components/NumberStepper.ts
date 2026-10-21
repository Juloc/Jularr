// The plus and minus of a number field. The native number input stays the source of precision, bounds and validation; the buttons only
// step it as the arrow keys do and tell the form it changed.

export function canStep(value: number, direction: number, minimum: string, maximum: string): boolean {
    if (!Number.isFinite(value)) return true;
    return direction > 0 ? maximum === "" || value < Number(maximum) : minimum === "" || value > Number(minimum);
}

export function initializeSteppers(root: ParentNode): void {
    root.querySelectorAll<HTMLElement>(".ui-field__control--number").forEach(control => {
        const input = control.querySelector<HTMLInputElement>(":scope > input[type=number]");
        const steps = Array.from(control.querySelectorAll<HTMLButtonElement>("[data-number-step]"));
        if (!input || steps.length === 0 || control.dataset.numberReady) return;
        control.dataset.numberReady = "true";
        const sync = () => steps.forEach(button => {
            button.disabled = input.disabled || input.readOnly || !canStep(input.valueAsNumber, Number(button.dataset.numberStep), input.min, input.max);
        });
        steps.forEach(button => button.addEventListener("click", () => {
            if (input.disabled || input.readOnly) return;
            if (Number(button.dataset.numberStep) > 0) input.stepUp();
            else input.stepDown();
            input.dispatchEvent(new Event("input", { bubbles: true }));
            input.dispatchEvent(new Event("change", { bubbles: true }));
        }));
        input.addEventListener("input", sync);
        input.addEventListener("change", sync);
        control.querySelector<HTMLElement>(".ui-field__stepper")!.hidden = false;
        sync();
    });
}
