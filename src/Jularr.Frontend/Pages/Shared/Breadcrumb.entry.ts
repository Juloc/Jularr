import { onPage } from "../../Modules/Navigation/PageSwap";

// A trail wider than its header scrolls sideways; it opens at the end, where the page's own title is.
onPage(import.meta.url, () => {
    document.querySelectorAll<HTMLElement>(".breadcrumb").forEach(trail => { trail.scrollLeft = trail.scrollWidth; });
});
