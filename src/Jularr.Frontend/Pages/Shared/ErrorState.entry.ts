import { onPage } from "../../Modules/Navigation/PageSwap";
import "./ErrorState.scss";

// Back is offered only where the tab has somewhere to go back to; without script it stays hidden and Home remains.
onPage(import.meta.url, () => {
    if (history.length > 1) document.querySelectorAll<HTMLElement>("[data-history-back]").forEach(button => { button.hidden = false; });
});

document.addEventListener("click", event => {
    if (event.target instanceof Element && event.target.closest("[data-history-back]")) history.back();
});
