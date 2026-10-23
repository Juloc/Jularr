// Installable, not offline: browsers offer to install an app only when a worker with a fetch handler controls it.
// The handler never answers, so every request (pages, htmx swaps, the live stream, sign-in redirects, range requests of
// the player) goes to the network exactly as without a worker, and nothing is cached. Offline use is a separate decision.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));
self.addEventListener("fetch", () => { });
