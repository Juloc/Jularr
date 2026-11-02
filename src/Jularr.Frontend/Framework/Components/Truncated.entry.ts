import "./Truncated.scss";
import { markTruncated, placeTip } from "./Truncated";
import { onPage } from "../../Modules/Navigation/PageSwap";

// Cut-off texts are measured again when the window changes size.
let resizing: ReturnType<typeof setTimeout> | undefined;
addEventListener("resize", () => {
    clearTimeout(resizing);
    resizing = setTimeout(() => markTruncated(document), 150);
});

onPage(import.meta.url, () => markTruncated(document), { partial: true });

// A cut-off text shows its full text in a tip that sits in the window, not in the row; it is placed when the pointer or the focus arrives.
for (const type of ["mouseover", "focusin"] as const) {
    document.addEventListener(type, event => {
        const element = (event.target as Element | null)?.closest<HTMLElement>(".truncated--clipped");
        if (element) placeTip(element);
    });
}
