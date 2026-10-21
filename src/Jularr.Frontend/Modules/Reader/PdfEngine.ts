import type { PDFDocumentProxy, PDFPageProxy, RenderTask } from 'pdfjs-dist';

// Mozilla's pdf.js (Apache-2.0) draws the original pages in the browser, scans with JPEG 2000 and JBIG2 included. Its script, its worker and its decoders ship with Jularr; nothing comes from a CDN.
// The decoders, character maps and fonts are copied beside the build by Tools/pdf-assets.mjs.
const Assets = '/pdfjs/';
let engine: Promise<typeof import('pdfjs-dist')> | undefined;
const documents = new Map<string, Promise<PDFDocumentProxy>>();

export type { PDFDocumentProxy, PDFPageProxy, RenderTask };

function load(): Promise<typeof import('pdfjs-dist')> {
    engine ??= import('pdfjs-dist/legacy/build/pdf.mjs').then(module => {
        module.GlobalWorkerOptions.workerPort = new Worker(new URL('./pdf.worker.ts', import.meta.url), { type: 'module' });
        return module as typeof import('pdfjs-dist');
    });
    return engine;
}

// One document per address; the reader's pages and the stand-ins in the reading text share it.
export function openPdf(url: string): Promise<PDFDocumentProxy> {
    let document = documents.get(url);
    if (!document) {
        document = load().then(pdf => pdf.getDocument({
            url, withCredentials: true, wasmUrl: `${Assets}wasm/`, cMapUrl: `${Assets}cmaps/`, cMapPacked: true, standardFontDataUrl: `${Assets}standard_fonts/`, iccUrl: `${Assets}iccs/`,
            enableXfa: false
        }).promise);
        documents.set(url, document);
        document.catch(() => documents.delete(url));
    }
    return document;
}

// The page drawn on the canvas at the scale; the task can be cancelled when another page is wanted.
export function drawPage(page: PDFPageProxy, canvas: HTMLCanvasElement, scale: number): RenderTask {
    const viewport = page.getViewport({ scale });
    canvas.width = Math.max(1, Math.floor(viewport.width));
    canvas.height = Math.max(1, Math.floor(viewport.height));
    return page.render({ canvas, viewport });
}

// The words of a page's own text layer, where it has one.
export async function pageWords(page: PDFPageProxy): Promise<string[]> {
    const content = await page.getTextContent();
    return content.items.flatMap(item => 'str' in item && item.str ? [item.str] : []);
}
