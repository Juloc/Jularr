// ASS and SSA subtitles are drawn by libass (JASSUB, WebAssembly) over the picture, with their fonts, positions and effects. The library loads
// only when a styled track is chosen; a browser that cannot run it keeps the plain WebVTT lines.
export interface StyledSubtitles {
    // Moves the subtitles against the picture, in seconds.
    setOffset(seconds: number): void;
    destroy(): void;
}

// The fonts are the addresses of the ones the file carries; libass takes them in, so the file's own lettering is drawn and the default font only stands in for the rest.
export async function showStyledSubtitles(video: HTMLVideoElement, url: string, offsetSeconds: number, fonts: string[] = []): Promise<StyledSubtitles> {
    const { default: JASSUB } = await import("jassub");
    const instance = new JASSUB({ video, subUrl: url, timeOffset: offsetSeconds, fonts });
    await instance.ready;
    return {
        setOffset: seconds => { instance.timeOffset = seconds; },
        destroy: () => { void instance.destroy(); }
    };
}
