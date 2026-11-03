import "./LayoutToggle.scss";
import { rememberDevicePreference, shareDevicePreference } from "../../Modules/Appearance/DevicePreference";

// The device's layout of personal lists; delegated from the document, so it also reaches pages swapped in later.
const layoutKey = 'list-layout';

function applyLayout(layout: string | null): void {
    if (layout === 'list') document.documentElement.dataset.listLayout = 'list';
    else delete document.documentElement.dataset.listLayout;
}

try {
    const stored = localStorage.getItem(layoutKey);
    if (stored) {
        applyLayout(stored);
        shareDevicePreference(layoutKey, stored);
    }
} catch {
    // Without storage the layout the page arrived in stays.
}

document.addEventListener('click', event => {
    if (!(event.target instanceof Element) || !event.target.closest('[data-layout-toggle]')) return;
    const list = document.documentElement.dataset.listLayout !== 'list';
    applyLayout(list ? 'list' : null);
    rememberDevicePreference(layoutKey, list ? 'list' : 'grid');
});
