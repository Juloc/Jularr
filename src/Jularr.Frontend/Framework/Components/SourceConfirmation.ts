// Optional enhancement: native manual paste remains available when clipboard access is blocked.
document.addEventListener("click", async event => {
    if (!(event.target instanceof Element)) return;
    const button = event.target.closest<HTMLButtonElement>("[data-source-paste]");
    if (!button) return;

    const input = button.closest(".source-assistance")?.querySelector<HTMLInputElement>('input[type="url"]');
    if (!input) return;

    try {
        const link = await navigator.clipboard.readText();
        if (/^https:\/\/\S+/i.test(link.trim())) {
            input.value = link.trim();
            input.dispatchEvent(new Event("input", { bubbles: true }));
        }
    } catch {
        // Insecure origins and restrictive browsers require native manual paste.
    }

    input.focus();
});
