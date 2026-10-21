import "./Truncated.scss";
import { markTruncated } from "./Truncated";
import { onPage } from "../../Modules/Navigation/PageSwap";

// Cut-off texts are measured again when the window changes size.
let resizing: ReturnType<typeof setTimeout> | undefined;
addEventListener("resize", () => {
    clearTimeout(resizing);
    resizing = setTimeout(() => markTruncated(document), 150);
});

onPage(import.meta.url, () => markTruncated(document));
