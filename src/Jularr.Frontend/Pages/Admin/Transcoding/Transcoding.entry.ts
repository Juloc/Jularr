import { onPage } from "../../../Modules/Navigation/PageSwap";

// A running benchmark is read again every few seconds until it has finished.
const Interval = 5_000;

onPage(import.meta.url, () => {
    if (!document.querySelector("[data-transcoding-running]")) return;
    window.setTimeout(() => {
        if (document.querySelector("[data-transcoding-running]")) location.reload();
    }, Interval);
});
