// Chooses the highlights a carousel shows from the device's preferences and moves between them.
// The progress bar of the current highlight is a CSS animation; its end moves on (see HeroCarousel.scss).

export interface HeroPreferences {
    perType: number;
    // 0 shows every highlight the preferences allow.
    max: number;
    withContinue: boolean;
    auto: boolean;
    paused: boolean;
}

export const heroDefaults: HeroPreferences = { perType: 2, max: 19, withContinue: true, auto: true, paused: false };
export const heroPreferencesChanged = 'jularr:hero-preferences';

export function readHeroPreferences(key: string): HeroPreferences {
    try {
        const stored = JSON.parse(localStorage.getItem(key) ?? 'null') as Partial<HeroPreferences> | null;
        if (!stored || typeof stored !== 'object') return { ...heroDefaults };
        const whole = (value: unknown, minimum: number, maximum: number, fallback: number) =>
            typeof value === 'number' && Number.isInteger(value) && value >= minimum && value <= maximum ? value : fallback;
        return {
            perType: whole(stored.perType, 1, 3, heroDefaults.perType),
            max: whole(stored.max, 0, 100, heroDefaults.max),
            withContinue: typeof stored.withContinue === 'boolean' ? stored.withContinue : heroDefaults.withContinue,
            auto: typeof stored.auto === 'boolean' ? stored.auto : heroDefaults.auto,
            paused: typeof stored.paused === 'boolean' ? stored.paused : heroDefaults.paused
        };
    } catch {
        return { ...heroDefaults };
    }
}

export function writeHeroPreferences(key: string, preferences: HeroPreferences): void {
    try {
        localStorage.setItem(key, JSON.stringify(preferences));
    } catch {
        // Preferences are optional; the carousel keeps working for this visit.
    }
    document.dispatchEvent(new CustomEvent(heroPreferencesChanged, { detail: key }));
}

const carousels = new Map<HTMLElement, () => void>();
const observer = typeof IntersectionObserver === 'undefined' ? undefined : new IntersectionObserver(entries => {
    for (const entry of entries) (entry.target as HTMLElement).toggleAttribute('data-away', !entry.isIntersecting);
}, { threshold: .15 });

const label = (template: string | undefined, ...values: number[]) => values.reduce((text, value, index) => text.replace('{' + index + '}', String(value)), template ?? '');

export function initializeHero(root: ParentNode = document): void {
    for (const [hero, apply] of carousels) {
        if (!hero.isConnected) {
            observer?.unobserve(hero);
            carousels.delete(hero);
        } else {
            apply();
        }
    }

    root.querySelectorAll<HTMLElement>('[data-hero-carousel]').forEach(hero => {
        if (!carousels.has(hero)) carousels.set(hero, setup(hero));
    });
}

function setup(hero: HTMLElement): () => void {
    const key = hero.dataset.heroPreferences ?? '';
    const slides = Array.from(hero.querySelectorAll<HTMLElement>('[data-hero-slide]'));
    const controls = hero.querySelector<HTMLElement>('.hero-carousel__controls')!;
    const segments = hero.querySelector<HTMLElement>('.hero-carousel__segments')!;
    const counter = hero.querySelector<HTMLElement>('.hero-carousel__counter')!;
    const pause = hero.querySelector<HTMLButtonElement>('[data-hero-pause]')!;
    const arrows = Array.from(hero.querySelectorAll<HTMLButtonElement>('[data-hero-step]'));
    let enabled: HTMLElement[] = slides;
    let index = 0;
    let touchStart: number | undefined;

    // Continued titles first, then each type's titles by rank, up to the maximum.
    function choose(preferences: HeroPreferences): HTMLElement[] {
        const chosen = slides.filter(slide => slide.hasAttribute('data-hero-continue') ? preferences.withContinue : Number(slide.dataset.heroPass) < preferences.perType);
        return preferences.max > 0 ? chosen.slice(0, preferences.max) : chosen;
    }

    function show(next: number): void {
        index = (next + enabled.length) % enabled.length;
        slides.forEach(slide => { slide.hidden = slide !== enabled[index]; });
        counter.textContent = `${index + 1} / ${enabled.length}`;
        segments.querySelectorAll<HTMLButtonElement>('.hero-carousel__segment').forEach((segment, position) => {
            const current = position === index;
            segment.classList.remove('hero-carousel__segment--current');
            if (current) {
                // A new bar starts its animation from the beginning.
                void segment.offsetWidth;
                segment.classList.add('hero-carousel__segment--current');
                segment.setAttribute('aria-current', 'true');
            } else {
                segment.removeAttribute('aria-current');
            }
        });
    }

    function apply(): void {
        const preferences = readHeroPreferences(key);
        const current = enabled[index];
        enabled = choose(preferences);
        hero.hidden = enabled.length === 0;
        hero.toggleAttribute('data-paused', preferences.paused || !preferences.auto);
        const stopped = preferences.paused || !preferences.auto;
        pause.setAttribute('aria-pressed', String(stopped));
        pause.setAttribute('aria-label', (stopped ? hero.dataset.heroResumeLabel : hero.dataset.heroPauseLabel) ?? '');
        if (enabled.length === 0) return;

        segments.replaceChildren(...enabled.map((_, position) => {
            const segment = document.createElement('button');
            segment.type = 'button';
            segment.className = 'hero-carousel__segment';
            segment.dataset.heroShow = String(position);
            segment.setAttribute('aria-label', label(hero.dataset.heroShowLabel, position + 1, enabled.length));
            segment.append(document.createElement('span'));
            return segment;
        }));
        const many = enabled.length > 1;
        controls.hidden = !many;
        arrows.forEach(arrow => { arrow.hidden = !many; });
        show(Math.max(0, enabled.indexOf(current)));
    }

    hero.addEventListener('click', event => {
        const target = event.target instanceof Element ? event.target : undefined;
        const step = target?.closest<HTMLElement>('[data-hero-step]');
        const segment = target?.closest<HTMLElement>('[data-hero-show]');
        if (step) show(index + Number(step.dataset.heroStep));
        else if (segment) show(Number(segment.dataset.heroShow));
        else if (target?.closest('[data-hero-pause]')) {
            const preferences = readHeroPreferences(key);
            const stopped = preferences.paused || !preferences.auto;
            writeHeroPreferences(key, { ...preferences, paused: !stopped, auto: true });
        }
    });
    hero.addEventListener('keydown', event => {
        if (event.target !== hero || (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight')) return;
        event.preventDefault();
        const rtl = getComputedStyle(hero).direction === 'rtl';
        show(index + (event.key === 'ArrowRight' !== rtl ? 1 : -1));
    });
    hero.addEventListener('touchstart', event => { touchStart = event.changedTouches[0]?.clientX; }, { passive: true });
    hero.addEventListener('touchend', event => {
        const distance = (event.changedTouches[0]?.clientX ?? 0) - (touchStart ?? 0);
        if (touchStart !== undefined && Math.abs(distance) > 45) show(index + (distance < 0 ? 1 : -1));
        touchStart = undefined;
    }, { passive: true });
    segments.addEventListener('animationend', event => {
        if (event.target instanceof Element && event.target.parentElement?.classList.contains('hero-carousel__segment--current')) show(index + 1);
    });

    observer?.observe(hero);
    apply();
    return apply;
}

document.addEventListener('visibilitychange', () => carousels.forEach((_, hero) => hero.toggleAttribute('data-hidden', document.hidden)));
document.addEventListener(heroPreferencesChanged, () => carousels.forEach(apply => apply()));
