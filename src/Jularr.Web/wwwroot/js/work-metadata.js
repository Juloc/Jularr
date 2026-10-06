// Persisted Work metadata on the Movie and Series pages (docs/mockups/movie-detail, anime-series-detail) and the trailer of the Discover
// Quick View (docs/mockups/media-preview): the trailer facade, the expandable description and artwork that is gone. The trailer is a
// click-to-load facade: nothing of YouTube is requested until the viewer starts it (or, in the Quick View, until it is open), and then
// exactly one sandboxed privacy-enhanced frame is added, built only from a key with the shape of a YouTube id. The rules are DOM-free where they can be (the frame description, the expand state), so they are testable without a page; the
// link of the facade opens the video on YouTube by itself when this script does not run.
(() => {
    "use strict";

    const youTubeKey = /^[A-Za-z0-9_-]{11}$/;
    const embedOrigin = "https://www.youtube-nocookie.com";

    /**
     * The attributes of the one frame the facade adds, or null for a key that is not a YouTube id. The sandbox gives the player what
     * it needs to run and nothing that reaches this page: no top navigation, no popups, no forms. A trailer that starts by itself is muted.
     */
    const trailerFrame = (key, title, muted = false) => {
        if (typeof key !== "string" || !youTubeKey.test(key)) {
            return null;
        }

        return {
            src: `${embedOrigin}/embed/${key}?autoplay=1${muted ? "&mute=1" : ""}&rel=0&playsinline=1`,
            title: title ?? "",
            sandbox: "allow-scripts allow-same-origin allow-presentation",
            allow: "autoplay; encrypted-media; picture-in-picture; fullscreen",
            referrerpolicy: "strict-origin-when-cross-origin"
        };
    };

    /**
     * Replaces the facade with the trailer frame; false (and nothing changes) when the facade does not carry a valid key. A trailer the viewer
     * did not ask for (muted) leaves the keyboard focus where it is.
     */
    const startTrailer = (facade, doc, { muted = false } = {}) => {
        const attributes = trailerFrame(facade.dataset.key, facade.dataset.frameTitle, muted);
        if (attributes === null) {
            return false;
        }

        const frame = doc.createElement("iframe");
        for (const [name, value] of Object.entries(attributes)) {
            frame.setAttribute(name, value);
        }

        frame.className = "vd-trailer-embed";
        facade.replaceChildren(frame);
        facade.dataset.started = "true";
        if (!muted) frame.focus();
        return true;
    };

    /** A click the facade takes over: the primary button without a modifier. Anything else (new tab, new window, middle click) follows the link. */
    const isPlainPrimaryClick = (event) => event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;

    /** Expands or collapses the description of a hero; the button keeps aria-expanded in step and its words say what it does next. */
    const toggleOverview = (root, button) => {
        const expanded = !root.classList.contains("is-expanded");
        root.classList.toggle("is-expanded", expanded);
        button.setAttribute("aria-expanded", expanded ? "true" : "false");
        button.textContent = expanded ? button.dataset.less : button.dataset.more;
        return expanded;
    };

    /** The button exists only for a description that is actually cut off, and stays while it is expanded. */
    const syncOverview = (root, text, button) => {
        button.hidden = !root.classList.contains("is-expanded") && text.scrollHeight <= text.clientHeight + 1;
    };

    /** Drops artwork whose file is gone; a hero then keeps the gradient it already sits on instead of a broken image. */
    const dropFailedArt = (image) => {
        const hero = image.closest("[data-work-hero]");
        image.remove();
        if (hero) {
            hero.classList.add("ad-hero-derived", "vd-hero-plain");
        }
    };

    window.JularrWorkMetadata = Object.freeze({ trailerFrame, startTrailer, isPlainPrimaryClick, toggleOverview, syncOverview, dropFailedArt });

    if (typeof document === "undefined" || typeof document.querySelectorAll !== "function") {
        return;
    }

    document.addEventListener("click", (event) => {
        const target = event.target instanceof Element ? event.target : null;
        const facade = target?.closest("[data-vd-trailer]");
        if (facade && target.closest("a") && isPlainPrimaryClick(event) && startTrailer(facade, document)) {
            event.preventDefault();
        }
    });

    for (const root of document.querySelectorAll("[data-vd-overview]")) {
        const text = root.querySelector(".ad-hero-description");
        const button = root.querySelector("[data-vd-expand]");
        if (!text || !button) {
            continue;
        }

        root.classList.add("is-clamped");
        const sync = () => syncOverview(root, text, button);
        button.addEventListener("click", () => {
            toggleOverview(root, button);
            sync();
        });
        window.addEventListener("resize", sync);
        sync();
    }

    document.addEventListener("error", (event) => {
        if (event.target instanceof HTMLImageElement && event.target.hasAttribute("data-work-art")) {
            dropFailedArt(event.target);
        }
    }, true);

    // Images that failed before this deferred script ran never fire again.
    for (const image of document.querySelectorAll("img[data-work-art]")) {
        if (image.complete && image.naturalWidth === 0) {
            dropFailedArt(image);
        }
    }
})();
