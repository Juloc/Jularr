// The queue a Chromecast plays: the item of the page and the ones that follow, each with a playback session of its own, so the device goes on by itself.
import type { CastEntry, CastSender, CastState } from "./Cast";

// An item that finished within this many seconds of its end counts as watched.
export const FinishedSeconds = 15;
// The device is given this many following items and gets them again as it moves on.
export const Ahead = 3;

export interface QueueTrack {
    key: string;
    label?: string;
    language: string | null;
    forced?: boolean;
    selected?: boolean;
}

// What the page knows of an item: its names and the tracks the server offers for it.
export interface QueueItem {
    assetId: number;
    title: string;
    context: string;
    audio: QueueTrack[];
    subtitles: QueueTrack[];
}

export interface QueuePlan {
    sessionId: string;
    resumeSeconds: number;
    durationSeconds: number;
}

export type Save = (value: object, leaving?: boolean) => void;

export interface QueueApi {
    // The asset first, then up to `following` items that follow it by the player's own rule.
    items(assetId: number, following: number): Promise<QueueItem[]>;
    plan(assetId: number, audioKey: string | null): Promise<QueuePlan>;
    // The address the device plays the session from; the subtitles of the same session hang below it.
    address(sessionId: string): Promise<string>;
    save(sessionId: string): Save;
}

// What the viewer chose, as a language, so the following episodes follow it.
export interface QueueChoice {
    // The item the two keys belong to; any other item follows the languages.
    assetId: number;
    audioKey: string | null;
    audioLanguage: string | null;
    subtitleKey: string | null;
    subtitleLanguage: string | null;
    subtitleForced: boolean;
}

export interface QueueEntry {
    item: QueueItem;
    sessionId: string;
    url: string;
    startSeconds: number;
    duration: number;
    position: number;
    audioKey: string | null;
    subtitles: QueueTrack[];
    activeSubtitle: number | null;
    save: Save;
    started: boolean;
    // A finished item has ended its session; the device cannot play it again from its old address.
    closed: boolean;
}

export interface QueueOptions {
    // Whether the device goes on to the next episode by itself; read whenever an item is made, the viewer may change it meanwhile.
    readonly autoplay: boolean;
    // The item that plays now changed (or the queue was built): names, buttons and the timeline follow it.
    changed(entry: QueueEntry, index: number): void;
    // The queue could not be made again.
    failed(): void;
}

export const isFinished = (position: number, duration: number): boolean => duration > 0 && position >= duration - FinishedSeconds;

// The track in the viewer's language; a forced track only stands in where no other has the language.
export function pickSubtitle(tracks: QueueTrack[], language: string | null, forced: boolean): number | null {
    if (!language) return null;
    const same = tracks.map((track, index) => ({ track, index })).filter(({ track }) => track.language === language);
    return (same.find(({ track }) => !!track.forced === forced) ?? same[0])?.index ?? null;
}

// The audio track in the viewer's language, else the one the profile's preferences pick for the item.
export function pickAudio(tracks: QueueTrack[], language: string | null): string | null {
    return (language ? tracks.find(track => track.language === language) : undefined)?.key ?? tracks.find(track => track.selected)?.key ?? null;
}

export class CastQueue {
    entries: QueueEntry[] = [];
    index = -1;
    choice: QueueChoice;
    // False once the item after the last one is known to be missing.
    hasMore = true;
    private refilling: Promise<void> | undefined;

    constructor(private readonly api: QueueApi, private readonly sender: CastSender, private readonly options: QueueOptions, choice: QueueChoice) {
        this.choice = choice;
    }

    get current(): QueueEntry | undefined {
        return this.entries[this.index];
    }

    get canNext(): boolean {
        return this.index + 1 < this.entries.length;
    }

    // The device gets the item and the ones after it. The first item's session must start; the others are made as far as the server has room.
    async open(assetId: number, startSeconds: number | null = null): Promise<void> {
        const items = await this.api.items(assetId, Ahead);
        const entries: QueueEntry[] = [await this.make(items[0], startSeconds)];
        for (const item of items.slice(1)) {
            try {
                entries.push(await this.make(item, null));
            } catch {
                break;
            }
        }
        this.hasMore = items.length === Ahead + 1;
        try {
            await this.sender.load(entries.map(toCast(this.options.autoplay)), 0);
        } catch (error) {
            for (const entry of entries) this.end(entry, entry.startSeconds, false);
            throw error;
        }
        this.entries = entries;
        this.index = -1;
        this.moveTo(0);
    }

    private async make(item: QueueItem, startSeconds: number | null): Promise<QueueEntry> {
        const choice = this.choice;
        const exact = choice.assetId === item.assetId;
        const audioKey = exact ? choice.audioKey : pickAudio(item.audio, choice.audioLanguage);
        const plan = await this.api.plan(item.assetId, audioKey);
        const address = await this.api.address(plan.sessionId);
        const activeSubtitle = exact ? (choice.subtitleKey ? item.subtitles.findIndex(track => track.key === choice.subtitleKey) : -1)
            : pickSubtitle(item.subtitles, choice.subtitleLanguage, choice.subtitleForced);
        return {
            item,
            sessionId: plan.sessionId,
            url: address,
            startSeconds: startSeconds ?? plan.resumeSeconds,
            duration: plan.durationSeconds,
            position: startSeconds ?? plan.resumeSeconds,
            audioKey: audioKey ?? pickAudio(item.audio, null),
            subtitles: item.subtitles,
            activeSubtitle: activeSubtitle !== null && activeSubtitle >= 0 ? activeSubtitle : null,
            save: this.api.save(plan.sessionId),
            started: false,
            closed: false
        };
    }

    // The device's state: the item that plays, its place, and the end of an item that has run out.
    update(state: CastState): void {
        const at = this.entries.findIndex(entry => entry.url === state.contentId);
        // The device has nothing left to play: the last item that ran out counts as watched.
        if (at < 0) {
            const last = this.current;
            if (state.contentId === "" && last && last.started && isFinished(last.position, last.duration)) this.leave(last);
            return;
        }
        if (at !== this.index) {
            this.moveTo(at);
            return;
        }
        const entry = this.entries[at];
        entry.position = state.currentTime;
        if (state.duration > 0) entry.duration = state.duration;
    }

    private moveTo(at: number): void {
        const previous = this.entries[this.index];
        this.index = at;
        const entry = this.entries[at];
        if (previous && previous !== entry) this.leave(previous);
        if (entry.closed) {
            // Its session ended with the finish: the same item is made again, from where the viewer meant to be.
            this.reopen(entry.item.assetId, null).catch(() => this.options.failed());
            return;
        }
        if (!entry.started) {
            entry.started = true;
            entry.save({ positionMs: Math.floor(entry.startSeconds * 1000), durationMs: toMs(entry.duration), isEnded: false, isCompleted: false, isStarted: true });
        }
        this.options.changed(entry, at);
        void this.refill();
    }

    // The device left an item: a finished one counts as watched and ends; another keeps its place and stays open, the viewer may come back to it.
    private leave(entry: QueueEntry): void {
        if (!entry.started || entry.closed) return;
        if (isFinished(entry.position, entry.duration)) {
            entry.closed = true;
            entry.save({ positionMs: toMs(entry.duration) ?? Math.floor(entry.position * 1000), durationMs: toMs(entry.duration), isEnded: true, isCompleted: true }, true);
        } else {
            this.saveEntry(entry, false);
        }
    }

    // The item that plays now keeps its place on its session.
    report(): void {
        if (this.current) this.saveEntry(this.current, false);
    }

    private saveEntry(entry: QueueEntry, ended: boolean): void {
        entry.save({ positionMs: Math.floor(entry.position * 1000), durationMs: toMs(entry.duration), isEnded: ended, isCompleted: false }, ended);
    }

    private end(entry: QueueEntry, position: number, completed: boolean): void {
        if (entry.closed) return;
        entry.closed = true;
        entry.save({ positionMs: Math.floor(position * 1000), durationMs: toMs(entry.duration), isEnded: true, isCompleted: completed }, true);
    }

    // The queue always holds about three items after the one that plays, as long as the Work has them.
    refill(): Promise<void> {
        this.refilling ??= this.fill().finally(() => { this.refilling = undefined; });
        return this.refilling;
    }

    private async fill(): Promise<void> {
        try {
            while (this.hasMore && this.entries.length - 1 - this.index < Ahead) {
                const last = this.entries[this.entries.length - 1];
                const following = (await this.api.items(last.item.assetId, 1))[1];
                if (!following) { this.hasMore = false; return; }
                const entry = await this.make(following, null);
                try {
                    await this.sender.append(toCast(this.options.autoplay)(entry, this.entries.length));
                } catch (error) {
                    this.end(entry, entry.startSeconds, false);
                    throw error;
                }
                this.entries.push(entry);
            }
        } catch {
            // The server may have no room for another session; the next change of item asks again.
        }
    }

    next(): void {
        if (this.canNext) this.sender.skip(1);
    }

    // The item before; false where there is none in the queue, and the page then builds the queue anew from the item before the first.
    previous(): boolean {
        if (this.index <= 0) return false;
        this.sender.skip(-1);
        return true;
    }

    setSubtitle(key: string | null): void {
        const entry = this.current;
        if (!entry) return;
        const at = key ? entry.subtitles.findIndex(track => track.key === key) : -1;
        entry.activeSubtitle = at >= 0 ? at : null;
        const track = at >= 0 ? entry.subtitles[at] : undefined;
        this.choice = { ...this.choice, assetId: entry.item.assetId, subtitleKey: key, subtitleLanguage: track?.language ?? null, subtitleForced: !!track?.forced };
        this.sender.setSubtitle(entry.activeSubtitle);
    }

    // Another audio track is another conversion: the item is made again at the same place, and the following ones follow the language.
    async setAudio(key: string): Promise<void> {
        const entry = this.current;
        if (!entry) return;
        const track = entry.item.audio.find(item => item.key === key);
        this.choice = { ...this.choice, assetId: entry.item.assetId, audioKey: key, audioLanguage: track?.language ?? null };
        await this.reopen(entry.item.assetId, entry.position);
    }

    // The queue is built anew from an item, the old sessions end where they stand.
    async reopen(assetId: number, startSeconds: number | null): Promise<void> {
        const old = this.entries;
        this.entries = [];
        this.index = -1;
        try {
            await this.open(assetId, startSeconds);
        } catch (error) {
            this.entries = old;
            throw error;
        }
        for (const entry of old) this.end(entry, entry.position, false);
    }

    // Casting is over: every session ends where it stands. What the viewer left is returned, so the page goes on there.
    close(): { assetId: number; position: number; finished: boolean } | null {
        const current = this.current;
        if (!current) return null;
        const finished = current.started && (current.closed || isFinished(current.position, current.duration));
        for (const entry of this.entries) this.end(entry, entry === current && finished && entry.duration > 0 ? entry.duration : entry.position, entry === current && finished);
        return { assetId: current.item.assetId, position: current.position, finished };
    }
}

const toMs = (seconds: number): number | null => seconds > 0 ? Math.round(seconds * 1000) : null;

// The first item starts at once; the next ones wait for the viewer where the profile turned autoplay off.
const toCast = (autoplay: boolean) => (entry: QueueEntry, index: number): CastEntry => ({
    url: entry.url,
    title: entry.item.title,
    subtitle: entry.item.context,
    startSeconds: entry.startSeconds,
    autoplay: index === 0 || autoplay,
    tracks: entry.subtitles.map(track => ({ url: subtitleUrl(entry.url, track.key), language: track.language ?? "und", name: track.label ?? track.key })),
    activeTrack: entry.activeSubtitle
});

// The subtitles of a session hang below its address: .../playlist.m3u8 becomes .../subtitles/<key>.
const subtitleUrl = (address: string, key: string): string => address.replace(/playlist\.m3u8$/, "subtitles/" + encodeURIComponent(key));
