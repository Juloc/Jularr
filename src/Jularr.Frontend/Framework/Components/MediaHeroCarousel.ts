const initialized = new WeakSet<HTMLElement>();
const intervalMs = 8500;

export function initializeHeroCarousels(root: ParentNode = document): void {
    root.querySelectorAll<HTMLElement>('[data-hero-carousel]').forEach(carousel => {
        if (initialized.has(carousel)) return;
        initialized.add(carousel);
        const slides = Array.from(carousel.querySelectorAll<HTMLElement>('[data-hero-slide-item]'));
        if (slides.length < 2) return;

        const segments = Array.from(carousel.querySelectorAll<HTMLButtonElement>('[data-hero-slide]'));
        const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
        let active = 0;
        let hovered = false;
        let focused = false;
        let visible = true;
        let timer: ReturnType<typeof setInterval> | undefined;
        let touchStart: number | undefined;

        function canRotate(): boolean {
            return carousel.isConnected && !reducedMotion.matches && !document.hidden
                && !hovered && !focused && visible;
        }

        function stop(): void {
            if (timer !== undefined) clearInterval(timer);
            timer = undefined;
        }

        function show(index: number): void {
            active = (index + slides.length) % slides.length;
            slides.forEach((slide, i) => {
                const current = i === active;
                slide.classList.toggle('is-active', current);
                slide.setAttribute('aria-hidden', current ? 'false' : 'true');
                if (current) slide.removeAttribute('inert');
                else slide.setAttribute('inert', '');
            });
            segments.forEach((segment, i) => segment.setAttribute('aria-current', i === active ? 'true' : 'false'));
        }

        function schedule(): void {
            stop();
            if (!carousel.isConnected) {
                observer?.disconnect();
                document.removeEventListener('visibilitychange', schedule);
                reducedMotion.removeEventListener?.('change', schedule);
                initialized.delete(carousel);
                return;
            }
            if (canRotate()) timer = setInterval(() => {
                if (!canRotate()) { stop(); return; }
                show(active + 1);
            }, intervalMs);
        }

        carousel.addEventListener('click', event => {
            if (!(event.target instanceof Element)) return;
            const direction = event.target.closest<HTMLElement>('[data-hero-direction]');
            const segment = event.target.closest<HTMLButtonElement>('[data-hero-slide]');
            if (direction) {
                show(active + (direction.dataset.heroDirection === 'previous' ? -1 : 1));
                schedule();
            } else if (segment) {
                show(Number(segment.dataset.heroSlide));
                schedule();
            }
        });
        carousel.addEventListener('mouseenter', () => { hovered = true; schedule(); });
        carousel.addEventListener('mouseleave', () => { hovered = false; schedule(); });
        carousel.addEventListener('focusin', () => { focused = true; schedule(); });
        carousel.addEventListener('focusout', event => {
            if (!carousel.contains(event.relatedTarget as Node | null)) {
                focused = false;
                schedule();
            }
        });
        carousel.addEventListener('touchstart', event => {
            touchStart = event.changedTouches[0]?.clientX;
        }, { passive: true });
        carousel.addEventListener('touchend', event => {
            const end = event.changedTouches[0]?.clientX;
            if (touchStart === undefined || end === undefined) return;
            const distance = end - touchStart;
            touchStart = undefined;
            if (Math.abs(distance) < 40) return;
            show(active + (distance < 0 ? 1 : -1));
            schedule();
        }, { passive: true });
        const observer = 'IntersectionObserver' in window
            ? new IntersectionObserver(entries => {
                visible = entries.some(entry => entry.isIntersecting);
                schedule();
            }, { threshold: 0.05 }) : undefined;
        observer?.observe(carousel);
        document.addEventListener('visibilitychange', schedule);
        reducedMotion.addEventListener?.('change', schedule);
        schedule();
    });
}
