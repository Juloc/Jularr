// A book's cover URL stays on the work after its file is gone, and external catalog covers can fail, so every
// <img data-book-cover data-title="..."> falls back to the same title placeholder instead of a broken image.
(() => {
    const showPlaceholder = (image) => {
        if (image.dataset.coverFailed) {
            return;
        }

        image.dataset.coverFailed = "true";
        const placeholder = document.createElement("span");
        placeholder.className = "book-cover-placeholder";
        placeholder.textContent = image.dataset.title || "";
        image.replaceWith(placeholder);
    };

    document.addEventListener("error", (event) => {
        if (event.target instanceof HTMLImageElement && event.target.hasAttribute("data-book-cover")) {
            showPlaceholder(event.target);
        }
    }, true);

    // Images that failed before this deferred script ran never fire again.
    for (const image of document.querySelectorAll("img[data-book-cover]")) {
        if (image.complete && image.naturalWidth === 0) {
            showPlaceholder(image);
        }
    }
})();
