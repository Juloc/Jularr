export interface SherpaModule {
    locateFile(path: string): string;
    onRuntimeInitialized(): void;
    onAbort(error: unknown): void;
    print?(text: string): void;
    printErr?(text: string): void;
    FS_createDataFile(path: string, name: null, bytes: Uint8Array, read: boolean, write: boolean, own: boolean): void;
    HEAPU8: Uint8Array;
}
export default function load(module: Partial<SherpaModule>): SherpaModule;
