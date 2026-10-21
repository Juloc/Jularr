// An image that fails to load (a missing local cover, a dead address) is replaced by the stand-in of the layout's template (NoCover.cs),
// which takes over the image's classes so it keeps the size the surrounding component gave the image.
const selector = "img.media-image, img[data-no-cover]";

function replace(image: HTMLImageElement): void {
    const template = document.querySelector<HTMLTemplateElement>("template[data-no-cover]");
    const kind = image.dataset.noCover === "person" ? "person" : "poster";
    const standIn = template?.content.querySelector<HTMLElement>(`[data-kind="${kind}"]`)?.cloneNode(true) as HTMLElement | undefined;
    if (!standIn || !image.isConnected) return;
    standIn.classList.add(...Array.from(image.classList));
    image.replaceWith(standIn);
}

// Errors do not bubble; the capture phase sees every image, also those a page swap brings later.
document.addEventListener("error", event => {
    if (event.target instanceof HTMLImageElement && event.target.matches(selector)) replace(event.target);
}, true);

// Images that failed before this script ran.
document.querySelectorAll<HTMLImageElement>(selector).forEach(image => {
    if (image.complete && image.naturalWidth === 0 && image.getAttribute("src")) replace(image);
});
