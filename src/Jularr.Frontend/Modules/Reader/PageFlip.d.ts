// Public API used from the pinned page-flip 2.0.7, which ships no declarations.
declare module 'page-flip' {
    export class PageFlip {
        constructor(element: HTMLElement, settings: {
            width: number; height: number; size: 'stretch'; minWidth: number; maxWidth: number;
            minHeight: number; maxHeight: number; usePortrait: boolean; autoSize: boolean;
            useMouseEvents: boolean; showPageCorners: boolean; drawShadow: boolean;
            maxShadowOpacity: number; flippingTime: number; startPage: number;
        });
        on(event: string, callback: (event: { data: unknown }) => void): this;
        loadFromHTML(pages: HTMLElement[]): void;
        updateFromHtml(pages: HTMLElement[]): void;
        turnToPage(index: number): void;
        flipNext(): void;
        flipPrev(): void;
        update(): void;
        getState(): string;
        getSettings(): { width: number; height: number; minWidth: number; maxWidth: number; minHeight: number; maxHeight: number; usePortrait: boolean };
        getRender(): { finishAnimation(): void };
        getFlipController(): { stopMove(): void };
        startUserTouch(point: { x: number; y: number }): void;
        userMove(point: { x: number; y: number }, touch: boolean): void;
        userStop(point: { x: number; y: number }, swipe?: boolean): void;
    }
}
