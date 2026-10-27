import { registerWorker } from "./Pwa";
import { watchAppVersion } from "./PwaUpdate";

// After the page has loaded, so registering never competes with the page's own requests.
function start(): void {
    void registerWorker(navigator.serviceWorker, window.isSecureContext);
    watchAppVersion();
}

if (document.readyState === "complete") start();
else window.addEventListener("load", start, { once: true });
