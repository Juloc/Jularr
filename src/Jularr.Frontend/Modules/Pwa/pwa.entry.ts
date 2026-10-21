import { registerWorker } from "./Pwa";

// After the page has loaded, so registering never competes with the page's own requests.
function start(): void {
    void registerWorker(navigator.serviceWorker, window.isSecureContext);
}

if (document.readyState === "complete") start();
else window.addEventListener("load", start, { once: true });
