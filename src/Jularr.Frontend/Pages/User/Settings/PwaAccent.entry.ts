import { onPage } from "../../../Modules/Navigation/PageSwap";

// The canonical form still works as a text field without JavaScript.
// Where supported, the browser's own Android/iOS/iPad color chooser is easier to operate.
onPage(import.meta.url, () => {
    for (const input of Array.from(document.querySelectorAll<HTMLInputElement>('input[name="AccentHex"]'))) {
        if (input.dataset.pwaColorEnhanced !== undefined) continue;
        input.dataset.pwaColorEnhanced = "";
        if (/^#[0-9a-f]{6}$/i.test(input.value)) input.type = "color";
    }
});
