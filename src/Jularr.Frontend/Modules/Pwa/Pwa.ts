// Makes the app installable on a phone or desktop: registers the pass-through worker (ServiceWorker.js, served at the site root).
// No offline mode: the worker caches nothing and never answers a request itself.

export const WorkerAddress = "/service-worker.js";

export interface WorkerContainer {
    register(address: string, options: { scope: string; updateViaCache: "none" }): Promise<unknown>;
}

// Registers the worker where the browser allows it (a secure context with service workers); a failure never disturbs the page.
export async function registerWorker(container: WorkerContainer | undefined, secure: boolean): Promise<boolean> {
    if (!container || !secure) return false;
    try {
        await container.register(WorkerAddress, { scope: "/", updateViaCache: "none" });
        return true;
    } catch {
        return false;
    }
}
