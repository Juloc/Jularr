import { heroDefaults, readHeroPreferences, writeHeroPreferences, type HeroPreferences } from '../../../Framework/Components/HeroCarousel';

// Device-local choices of Discover. Everything is delegated from the document, so it also reaches HTML a page script inserts later.
const heroKey = 'discover-hero';
const layoutKey = 'discover-layout';

// Controls that need script show with it.
document.documentElement.classList.add('discover-scripted');

function applyLayout(layout: string | null): void {
    if (layout === 'grid') document.documentElement.dataset.discoverLayout = 'grid';
    else delete document.documentElement.dataset.discoverLayout;
    document.querySelectorAll<HTMLInputElement>('[data-discover-layout-choice]').forEach(input => { input.checked = input.value === (layout === 'grid' ? 'grid' : 'rows'); });
}

function storeLayout(grid: boolean): void {
    applyLayout(grid ? 'grid' : null);
    try {
        localStorage.setItem(layoutKey, grid ? 'grid' : 'rows');
    } catch {
        // The layout then applies to this visit only.
    }
}

try {
    applyLayout(localStorage.getItem(layoutKey));
} catch {
    // Without storage the shelves stay in rows.
}

// The panel shows what the carousel and the shelves use now.
function showPreferences(): void {
    const preferences = readHeroPreferences(heroKey);
    document.querySelectorAll<HTMLInputElement>('[data-hero-pref]').forEach(input => {
        const value = preferences[input.dataset.heroPref as keyof HeroPreferences];
        if (input.type === 'checkbox') input.checked = input.dataset.heroPref === 'auto' ? preferences.auto && !preferences.paused : preferences.withContinue;
        else input.checked = input.value === String(value);
    });
    applyLayout(document.documentElement.dataset.discoverLayout ?? null);
}

// The count and the button of an area follow its checkboxes; so does the one for all types.
function syncGroup(group: Element | null): void {
    if (!group) return;
    const boxes = Array.from(group.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));
    const checked = boxes.filter(box => box.checked).length;
    const pick = group.querySelector<HTMLElement>('[data-discover-pick]');
    if (pick) pick.textContent = (checked === boxes.length ? pick.dataset.clearLabel : pick.dataset.pickLabel) ?? '';
    const count = group.querySelector<HTMLElement>('.choice-group__count');
    if (count?.dataset.countLabel) count.textContent = count.dataset.countLabel.replace('{0}', String(checked)).replace('{1}', String(boxes.length));
    const all = group.closest('.drawer__section')?.querySelector('[data-discover-all]');
    const every = Array.from(group.closest('.drawer__section')?.querySelectorAll<HTMLInputElement>('input[name="type"]') ?? []);
    all?.setAttribute('aria-pressed', String(every.length > 0 && every.every(box => box.checked)));
}

document.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;

    if (event.target.closest('[data-drawer-pane="view"]')) showPreferences();

    if (event.target.closest('[data-hero-reset]')) {
        writeHeroPreferences(heroKey, { ...heroDefaults });
        storeLayout(false);
        showPreferences();
    }

    if (event.target.closest('[data-discover-layout]')) storeLayout(document.documentElement.dataset.discoverLayout !== 'grid');

    // Selects every type of an area, or none when all are chosen.
    const pick = event.target.closest<HTMLElement>('[data-discover-pick]');
    if (pick) {
        const group = pick.closest('.choice-group');
        const boxes = Array.from(group?.querySelectorAll<HTMLInputElement>('input[type="checkbox"]') ?? []);
        const all = boxes.every(box => box.checked);
        boxes.forEach(box => { box.checked = !all; });
        syncGroup(group);
    }

    // Selects every type, whatever the area.
    const every = event.target.closest<HTMLElement>('[data-discover-all]');
    if (every) {
        const section = every.closest('.drawer__section');
        section?.querySelectorAll<HTMLInputElement>('input[name="type"]').forEach(box => { box.checked = true; });
        section?.querySelectorAll('.choice-group').forEach(syncGroup);
    }
});

document.addEventListener('change', event => {
    const input = event.target;
    if (!(input instanceof HTMLInputElement)) return;
    if (input.name === 'type' && input.type === 'checkbox') syncGroup(input.closest('.choice-group'));
    if (input.dataset.discoverLayoutChoice !== undefined) storeLayout(input.value === 'grid');
    if (!input.dataset.heroPref) return;

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
