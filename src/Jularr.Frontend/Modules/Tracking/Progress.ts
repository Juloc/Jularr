// One in-flight save, latest pending state.
export function progressReporter(url: string, antiforgery: string, feedback: (failed: boolean) => void): (value: object, leaving?: boolean) => void {
    let acknowledged = '';
    let pending: { body: string; leaving: boolean } | undefined;
    let sending = false;
    let retries = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function send(): Promise<void> {
        if (sending || !pending) return;
        const next = pending;
        pending = undefined;
        sending = true;
        let retry = true;
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 10_000);

        try {
            const response = await fetch(url, {
                method: 'PUT', keepalive: true,
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: antiforgery },
                body: next.body, signal: controller.signal
            });
            retry = response.status === 408 || response.status === 429 || response.status >= 500;
            if (!response.ok) throw new Error('Progress save failed');
            acknowledged = next.body;
            retries = 0;
            feedback(false);
        } catch {
            acknowledged = '';
            feedback(true);
            // Retry transient failures twice.
            if (!pending && retry && !next.leaving && retries++ < 2) {
                pending = next;
                timer = setTimeout(() => void send(), 2000);
            }
        } finally {
            clearTimeout(timeout);
            sending = false;
            if (pending?.body === acknowledged) pending = undefined;
            if (pending && pending !== next) void send();
        }
    }

    return (value, leaving = false) => {
        const body = JSON.stringify(value);
        if (body === acknowledged && !pending) return;
        clearTimeout(timer);
        pending = { body, leaving };
        retries = 0;
        void send();
    };
}
