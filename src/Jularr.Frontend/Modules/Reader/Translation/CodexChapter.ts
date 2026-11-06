// Translating a chapter with Codex on a button press: the server answers one stretch of blocks per request (a chunk), the chapter goes in as many requests as it needs,
// each answer is shown as it arrives, a failed request keeps what the others brought, and the time left comes from how fast the last requests were.

export type Problem = "limit" | "notConnected" | "busy" | "failed";

export interface Chunk {
    blocks: { index: number; text: string }[];
    failed: number[];
    characters: number;
    seconds: number;
    remaining: number;
    remainingCharacters: number;
}

export interface Sample { characters: number; seconds: number }

// The cause of a failed request, from the error code the server names in its answer.
export function problemOf(body: string): Problem {
    if (body.includes("translation_limit_reached")) return "limit";
    if (body.includes("translation_not_connected")) return "notConnected";
    if (body.includes("translation_local_busy")) return "busy";
    return "failed";
}

// Seconds left for the characters still to translate, at the speed of the last three requests; undefined before one was measured.
export function secondsLeft(samples: Sample[], remainingCharacters: number): number | undefined {
    const recent = samples.slice(-3);
    const characters = recent.reduce((sum, sample) => sum + sample.characters, 0);
    const seconds = recent.reduce((sum, sample) => sum + sample.seconds, 0);
    return characters > 0 && seconds > 0 ? remainingCharacters * seconds / characters : undefined;
}

// Whole minutes, rounded; 0 means less than a minute.
export function minutesLeft(seconds: number): number {
    return seconds < 45 ? 0 : Math.round(seconds / 60);
}

export interface ChapterRun {
    // The first block to translate (the one in view); the rest follows, then the start of the chapter.
    from: number;
    post(from: number, skip: number[]): Promise<Response>;
    // Blocks that arrived with the request.
    arrived(blocks: { index: number; text: string }[]): void;
    // After every request: the characters still to translate, the time left when known, and whether the server is busy (waiting for its lane).
    progress(state: { remainingCharacters: number; seconds: number | undefined; busy: boolean }): void;
    stopped(): boolean;
    pause(milliseconds: number): Promise<void>;
}

export interface ChapterResult {
    state: "done" | "stopped" | "error";
    problem?: Problem;
    // Blocks whose answers failed the server's checks and stay in the original language.
    failed: number[];
    translated: number;
}

const BusyPauseMs = 4000;
const BusyRetries = 15;

export async function translateChapter(run: ChapterRun): Promise<ChapterResult> {
    const skip: number[] = [];
    const samples: Sample[] = [];
    let from = run.from;
    let translated = 0;
    let busy = 0;
    while (!run.stopped()) {
        let response: Response;
        try {
            response = await run.post(from, skip);
        } catch {
            return { state: "error", problem: "failed", failed: skip, translated };
        }
        if (run.stopped()) break;
        if (!response.ok) {
            const problem = problemOf(await response.text().catch(() => ""));
            // The server's one lane is taken by another translation: wait a moment and ask again.
            if (problem === "busy" && ++busy <= BusyRetries) {
                run.progress({ remainingCharacters: 0, seconds: undefined, busy: true });
                await run.pause(BusyPauseMs);
                continue;
            }
            return { state: "error", problem, failed: skip, translated };
        }

        busy = 0;
        const chunk = await response.json() as Chunk;
        if (run.stopped()) break;
        if (chunk.blocks.length > 0) {
            translated += chunk.blocks.length;
            run.arrived(chunk.blocks);
            samples.push({ characters: chunk.characters, seconds: chunk.seconds });
        }
        skip.push(...chunk.failed);
        run.progress({ remainingCharacters: chunk.remainingCharacters, seconds: secondsLeft(samples, chunk.remainingCharacters), busy: false });
        if (chunk.remaining === 0 || chunk.blocks.length === 0 && chunk.failed.length === 0) return { state: "done", failed: skip, translated };
        from = Math.max(...chunk.blocks.map(block => block.index), ...chunk.failed) + 1;
    }

    return { state: "stopped", failed: skip, translated };
}
