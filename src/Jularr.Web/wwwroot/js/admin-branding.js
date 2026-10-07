// Admin → Appearance → Branding: the brand colour. The picker sets a colour; the instance stores its hue and derives the accent from it
// (BrandingColor.SeedOf on the server; seedOf below is the same formula so the readout shows what will really be applied). The preview is the
// real palette: the page itself is repainted with the server-derived accent through the same endpoint Settings → Appearance uses, so contrast
// and every derived token are exactly what the instance will show. Nothing is saved until the form is submitted.
(() => {
    const form = document.querySelector("[data-brand-colour]");
    if (!form) {
        return;
    }

    const toggle = form.querySelector("[data-brand-colour-toggle]");
    const choice = form.querySelector("[data-brand-colour-choice]");
    const picker = form.querySelector("[data-brand-colour-picker]");
    const hueInput = form.querySelector("[data-brand-hue]");
    const readout = form.querySelector("[data-brand-colour-hex]");
    const fromLogo = form.querySelector("[data-brand-colour-from-logo]");
    const note = form.querySelector("[data-brand-colour-note]");
    const recolour = form.querySelector("[data-brand-recolour]");
    const logoOriginal = form.querySelector("[data-brand-logo-original]");
    const logoTint = form.querySelector("[data-brand-logo-tint]");
    const palette = document.getElementById("app-accent-palette");
    const original = palette?.textContent ?? "";
    const previewUrl = form.dataset.previewUrl;
    let controller = null;

    const hueOf = (hex) => {
        const red = parseInt(hex.slice(1, 3), 16) / 255;
        const green = parseInt(hex.slice(3, 5), 16) / 255;
        const blue = parseInt(hex.slice(5, 7), 16) / 255;
        return rgbHue(red, green, blue);
    };

    const rgbHue = (red, green, blue) => {
        const max = Math.max(red, green, blue);
        const delta = max - Math.min(red, green, blue);
        if (delta === 0) {
            return 0;
        }

        const raw = max === red ? ((green - blue) / delta) % 6 : max === green ? (blue - red) / delta + 2 : (red - green) / delta + 4;
        return Math.round(((raw * 60) % 360 + 360) % 360) % 360;
    };

    const seedOf = (hue) => {
        const saturation = 0.62;
        const lightness = 0.46;
        const chroma = (1 - Math.abs(2 * lightness - 1)) * saturation;
        const section = hue / 60;
        const second = chroma * (1 - Math.abs((section % 2) - 1));
        const [red, green, blue] = [[chroma, second, 0], [second, chroma, 0], [0, chroma, second], [0, second, chroma], [second, 0, chroma], [chroma, 0, second]][Math.min(5, Math.floor(section))];
        const offset = lightness - chroma / 2;
        const channel = (value) => Math.round((value + offset) * 255).toString(16).padStart(2, "0");
        return `#${channel(red)}${channel(green)}${channel(blue)}`;
    };

    const paint = async (accent) => {
        if (!palette || !previewUrl) {
            return;
        }

        controller?.abort();
        controller = new AbortController();
        try {
            const url = new URL(previewUrl, window.location.origin);
            url.searchParams.set("accent", accent);
            const response = await fetch(url, { credentials: "same-origin", signal: controller.signal, headers: { Accept: "text/css" } });
            if (response.ok) {
                palette.textContent = await response.text();
            }
        } catch (error) {
            if (error.name !== "AbortError") {
                throw error;
            }
        }
    };

    // The original logo while the brand colour or the recolouring is off, the silhouette in the (previewed) accent while both are on.
    const showLogo = () => {
        const tinted = toggle.checked && recolour !== null && recolour.checked && !recolour.disabled;
        if (logoOriginal && logoTint) {
            logoOriginal.hidden = tinted;
            logoTint.hidden = !tinted;
        }
    };

    const apply = () => {
        const on = toggle.checked;
        choice.hidden = !on;
        showLogo();
        if (!on) {
            controller?.abort();
            if (palette) {
                palette.textContent = original;
            }

            return;
        }

        const hue = hueOf(picker.value);
        hueInput.value = String(hue);
        const applied = seedOf(hue);
        readout.textContent = applied;
        void paint(applied);
    };

    // The most vivid, most frequent hue of the logo's visible pixels, found on a small canvas; nothing leaves the browser.
    const hueOfLogo = (image) => {
        const size = 48;
        const canvas = document.createElement("canvas");
        canvas.width = size;
        canvas.height = size;
        const context = canvas.getContext("2d", { willReadFrequently: true });
        context.drawImage(image, 0, 0, size, size);
        const bins = new Array(36).fill(0);
        const { data } = context.getImageData(0, 0, size, size);
        for (let index = 0; index < data.length; index += 4) {
            const [red, green, blue] = [data[index] / 255, data[index + 1] / 255, data[index + 2] / 255];
            const max = Math.max(red, green, blue);
            const min = Math.min(red, green, blue);
            const lightness = (max + min) / 2;
            const saturation = max === min ? 0 : (max - min) / (1 - Math.abs(2 * lightness - 1));
            if (data[index + 3] > 128 && saturation > 0.25 && lightness > 0.12 && lightness < 0.88) {
                bins[Math.floor(rgbHue(red, green, blue) / 10) % 36] += saturation;
            }
        }

        const best = bins.indexOf(Math.max(...bins));
        return bins[best] > 0 ? best * 10 + 5 : null;
    };

    toggle.addEventListener("change", apply);
    picker.addEventListener("input", apply);
    recolour?.addEventListener("change", showLogo);
    fromLogo?.addEventListener("click", () => {
        const image = form.closest("section").querySelector("[data-brand-logo]");
        if (!image) {
            return;
        }

        const choose = () => {
            let hue = null;
            try {
                hue = hueOfLogo(image);
            } catch {
                hue = null;
            }

            if (note) {
                note.hidden = hue !== null;
            }

            if (hue !== null) {
                picker.value = seedOf(hue);
                toggle.checked = true;
                apply();
            }
        };

        if (image.complete && image.naturalWidth > 0) {
            choose();
        } else {
            image.addEventListener("load", choose, { once: true });
        }
    });

    // Leaving the page without saving must not leave a previewed accent behind on a page that stays in the browser's cache.
    window.addEventListener("pagehide", () => {
        if (palette) {
            palette.textContent = original;
        }
    });

    choice.hidden = !toggle.checked;
    showLogo();
})();
