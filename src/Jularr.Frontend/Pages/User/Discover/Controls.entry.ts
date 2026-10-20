import { heroDefaults, readHeroPreferences, writeHeroPreferences, type HeroPreferences } from '../../../Framework/Components/HeroCarousel';

// Device-local choices of Discover. Everything is delegated from the document, so it also reaches HTML a page script inserts later.
const heroKey = 'discover-hero';
const layoutKey = 'discover-layout';

// Controls that need script show with it.
document.documentElement.classList.add('discover-scripted');

function applyLayout(layout: string | null): void {
    if (layout === 'grid') document.documentElement.dataset.discoverLayout = 'grid';
    else delete document.documentElement.dataset.discoverLayout;
}

try {
    applyLayout(localStorage.getItem(layoutKey));
} catch {
    // Without storage the shelves stay in rows.
}

// The drawer shows what the carousel uses now.
function showHeroPreferences(): void {
    const preferences = readHeroPreferences(heroKey);
    document.querySelectorAll<HTMLInputElement>('[data-hero-pref]').forEach(input => {
        const value = preferences[input.dataset.heroPref as keyof HeroPreferences];
        if (input.type === 'checkbox') input.checked = input.dataset.heroPref === 'auto' ? preferences.auto && !preferences.paused : preferences.withContinue;
        else input.checked = input.value === String(value);
    });
}

document.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;

    if (event.target.closest('[data-drawer-open="discover-view"]')) showHeroPreferences();

    if (event.target.closest('[data-hero-reset]')) {
        writeHeroPreferences(heroKey, { ...heroDefaults });
        showHeroPreferences();
    }

    if (event.target.closest('[data-discover-layout]')) {
        const grid = document.documentElement.dataset.discoverLayout !== 'grid';
        applyLayout(grid ? 'grid' : null);
        try {
            localStorage.setItem(layoutKey, grid ? 'grid' : 'rows');
        } catch {
            // The layout then applies to this visit only.
        }
    }

    // Selects every type of an area, or none when all are chosen.
    const pick = event.target.closest<HTMLElement>('[data-discover-pick]');
    if (pick) {
        const boxes = Array.from(pick.closest('.choice-group')?.querySelectorAll<HTMLInputElement>('input[type="checkbox"]') ?? []);
        const all = boxes.every(box => box.checked);
        boxes.forEach(box => { box.checked = !all; });
        labelPick(pick, !all);
    }
});

function labelPick(pick: HTMLElement, all: boolean): void {
    pick.textContent = (all ? pick.dataset.clearLabel : pick.dataset.pickLabel) ?? '';
}

document.addEventListener('change', event => {
    const input = event.target;
    if (input instanceof HTMLInputElement && input.name === 'type') {
        const group = input.closest('.choice-group');
        const pick = group?.querySelector<HTMLElement>('[data-discover-pick]');
        if (group && pick) labelPick(pick, Array.from(group.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')).every(box => box.checked));
    }

    if (!(input instanceof HTMLInputElement) || !input.dataset.heroPref) return;
    const preferences = readHeroPreferences(heroKey);
    switch (input.dataset.heroPref) {
        case 'perType': preferences.perType = Number(input.value); break;
        case 'max': preferences.max = Number(input.value); break;
        case 'withContinue': preferences.withContinue = input.checked; break;
        case 'auto': preferences.auto = input.checked; preferences.paused = false; break;
    }
    writeHeroPreferences(heroKey, preferences);
});

// The slash focuses the search, as in most search pages.
document.addEventListener('keydown', event => {
    const target = event.target;
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement
        || target instanceof HTMLSelectElement || (target instanceof HTMLElement && target.isContentEditable)) return;
    const search = document.querySelector<HTMLInputElement>('.app-header .search-box input[type="search"]');
    if (!search) return;
    event.preventDefault();
    search.focus();
});
