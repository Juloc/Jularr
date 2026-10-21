// The eye beside a password field switches it between hidden and readable.
document.addEventListener("click", event => {
    const button = event.target instanceof Element ? event.target.closest<HTMLButtonElement>("[data-password-reveal]") : null;
    const input = button?.parentElement?.querySelector<HTMLInputElement>("input");
    if (!button || !input) return;

    const show = input.type === "password";
    input.type = show ? "text" : "password";
    button.setAttribute("aria-pressed", String(show));
    button.setAttribute("aria-label", (show ? button.dataset.hideLabel : button.dataset.showLabel) ?? "");
});
