import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

function load(name, globals = {}) {
    const source = readFileSync(new URL(`../Modules/Player/${name}.ts`, import.meta.url), 'utf8');
    const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const exports = {};
    runInNewContext(script, { exports, ...globals });
    return exports;
}

const queue = load('CastQueue');
// Values made inside the vm context have their own prototypes.
const plain = value => JSON.parse(JSON.stringify(value));

// A fake of Google's sender library: what the page asks of it is recorded.
function fakeCast() {
    const calls = [];
    const listeners = {};
    const remote = { isConnected: true, isPaused: false, currentTime: 0, duration: 0, volumeLevel: 0.5, isMuted: false, mediaInfo: null };
    const mediaSession = {
        queueAppendItem: (item, ok) => { calls.push(['append', item]); ok(); },
        queueNext: ok => { calls.push(['next']); ok(); },
        queuePrev: ok => { calls.push(['prev']); ok(); },
        editTracksInfo: (request, ok) => { calls.push(['tracks', request.activeTrackIds]); ok(); }
    };
    const session = {
        getSessionObj: () => ({ queueLoad: (request, ok) => { calls.push(['load', request]); ok(); } }),
        getMediaSession: () => mediaSession,
        getCastDevice: () => ({ friendlyName: 'Living room' })
    };
    class Item { constructor(mediaInfo) { this.media = mediaInfo; } }
    const chromeGlobal = {
        cast: {
            AutoJoinPolicy: { ORIGIN_SCOPED: 'origin_scoped' },
            media: {
                DEFAULT_MEDIA_RECEIVER_APP_ID: 'CC1AD845',
                StreamType: { BUFFERED: 'BUFFERED' },
                HlsSegmentFormat: { TS: 'ts' },
                TrackType: { TEXT: 'TEXT' },
                TextTrackType: { SUBTITLES: 'SUBTITLES' },
                RepeatMode: { OFF: 'REPEAT_OFF' },
                MediaInfo: class { constructor(contentId, contentType) { this.contentId = contentId; this.contentType = contentType; } },
                GenericMediaMetadata: class {},
                Track: class { constructor(trackId, trackType) { this.trackId = trackId; this.type = trackType; } },
                QueueItem: Item,
                QueueLoadRequest: class { constructor(items) { this.items = items; } },
                EditTracksInfoRequest: class { constructor(activeTrackIds) { this.activeTrackIds = activeTrackIds; } }
            }
        }
    };
    const castGlobal = {
        framework: {
            CastContext: {
                getInstance: () => ({
                    setOptions: options => calls.push(['options', options]),
                    addEventListener: (type, handler) => { listeners[type] = handler; },
                    getCurrentSession: () => session,
                    requestSession: async () => undefined,
                    endCurrentSession: stopCasting => calls.push(['end', stopCasting])
                })
            },
            RemotePlayer: class { constructor() { return remote; } },
            RemotePlayerController: class {
                addEventListener(type, handler) { listeners[type] = handler; }
                playOrPause() { calls.push(['toggle']); }
                seek() { calls.push(['seek', remote.currentTime]); }
                setVolumeLevel() { calls.push(['volume', remote.volumeLevel]); }
                muteOrUnmute() { calls.push(['mute']); }
            },
            RemotePlayerEventType: { ANY_CHANGE: 'any' },
            CastContextEventType: { SESSION_STATE_CHANGED: 'state' }
        }
    };
    const window = {};
    const document = { createElement: () => ({}), head: { append: () => window.__onGCastApiAvailable(true) } };
    return { calls, listeners, remote, globals: { window, document, chrome: chromeGlobal, cast: castGlobal } };
}

const entry = (name, extra = {}) => ({ url: `https://jularr/api/v1/cast/${name}/playlist.m3u8`, title: 'Show', subtitle: name, startSeconds: 0, autoplay: true, tracks: [], activeTrack: null, ...extra });

test('The sender loads the episodes as one queue with their subtitles and the place to start', async () => {
    const fake = fakeCast();
    const { loadCast } = load('Cast', fake.globals);
    const states = [];
    const sender = await loadCast(state => states.push(state));
    await sender.load([
        entry('one', { startSeconds: 61, tracks: [{ url: 'https://jularr/one/subtitles/s3', language: 'de', name: 'Deutsch' }], activeTrack: 0 }),
        entry('two', { autoplay: false })
    ], 0);

    assert.deepEqual(plain(fake.calls[0]), ['options', { receiverApplicationId: 'CC1AD845', autoJoinPolicy: 'origin_scoped' }]);
    const request = fake.calls.find(call => call[0] === 'load')[1];
    assert.equal(request.startIndex, 0);
    assert.equal(request.repeatMode, 'REPEAT_OFF');
    const [first, second] = request.items;
    assert.equal(first.media.contentType, 'application/x-mpegURL');
    assert.equal(first.startTime, 61);
    assert.equal(first.autoplay, true);
    assert.equal(second.autoplay, false);
    assert.deepEqual([...first.activeTrackIds], [1]);
    assert.deepEqual([...second.activeTrackIds], []);
    const track = first.media.tracks[0];
    assert.equal(track.trackContentId, 'https://jularr/one/subtitles/s3');
    assert.equal(track.trackContentType, 'text/vtt');
    assert.equal(track.language, 'de');
    assert.equal(first.media.metadata.subtitle, 'one');
});

test('The sender adds an episode, skips, changes the subtitle and the volume on the device', async () => {
    const fake = fakeCast();
    const { loadCast } = load('Cast', fake.globals);
    const sender = await loadCast(() => undefined);
    await sender.append(entry('three'));
    sender.skip(1);
    sender.skip(-1);
    sender.setSubtitle(1);
    sender.setSubtitle(null);
    sender.setVolume(1.7);
    sender.setMuted(true);
    fake.remote.isMuted = true;
    sender.setMuted(true);

    assert.equal(fake.calls.find(call => call[0] === 'append')[1].media.contentId, 'https://jularr/api/v1/cast/three/playlist.m3u8');
    assert.deepEqual(fake.calls.filter(call => ['next', 'prev'].includes(call[0])).map(call => call[0]), ['next', 'prev']);
    assert.deepEqual(fake.calls.filter(call => call[0] === 'tracks').map(call => [...call[1]]), [[2], []]);
    assert.deepEqual(fake.calls.find(call => call[0] === 'volume'), ['volume', 1]);
    // Muting toggles on the device, so a device that is muted already is left as it is.
    assert.equal(fake.calls.filter(call => call[0] === 'mute').length, 1);
});

test('The sender publishes the device state with the address of the item that plays', async () => {
    const fake = fakeCast();
    const { loadCast } = load('Cast', fake.globals);
    const states = [];
    await loadCast(state => states.push(state));
    fake.remote.mediaInfo = { contentId: 'https://jularr/two' };
    fake.remote.currentTime = 12;
    fake.listeners.any();
    assert.equal(states.at(-1).contentId, 'https://jularr/two');
    assert.equal(states.at(-1).device, 'Living room');
    assert.equal(states.at(-1).volume, 0.5);
    fake.remote.mediaInfo = null;
    fake.listeners.any();
    assert.equal(states.at(-1).contentId, '');
});

test('Tracks follow the language of the viewer: audio by language, a subtitle by language with a forced one standing in only where nothing else has it', () => {
    const subtitles = [{ key: 's3', language: 'en' }, { key: 's4', language: 'de', forced: true }, { key: 's5', language: 'de' }];
    assert.equal(queue.pickSubtitle(subtitles, 'de', false), 2);
    assert.equal(queue.pickSubtitle(subtitles, 'de', true), 1);
    assert.equal(queue.pickSubtitle(subtitles, 'fr', false), null);
    assert.equal(queue.pickSubtitle(subtitles, null, false), null);
    assert.equal(queue.pickSubtitle([{ key: 's4', language: 'de', forced: true }], 'de', false), 0);
    const audio = [{ key: '1', language: 'en' }, { key: '2', language: 'de' }, { key: '3', language: 'fr', selected: true }];
    assert.equal(queue.pickAudio(audio, 'de'), '2');
    assert.equal(queue.pickAudio(audio, 'ja'), '3');
    assert.equal(queue.pickAudio(audio, null), '3');
    assert.equal(queue.pickAudio([{ key: '1', language: 'en' }], 'ja'), null);
});

test('An item that ran to its end counts as finished, one left earlier does not', () => {
    assert.equal(queue.isFinished(2640, 2650), true);
    assert.equal(queue.isFinished(1200, 2650), false);
    assert.equal(queue.isFinished(10, 0), false);
});

// Episodes 1 to 6 of a season; each has a German and an English audio track and subtitle.
function world(options = {}) {
    const total = options.total ?? 6;
    const item = number => ({
        assetId: number, title: 'Show', context: `Episode ${number}`,
        audio: [{ key: '1', language: 'en', selected: true }, { key: '2', language: 'de' }],
        subtitles: [{ key: 's3', label: 'English', language: 'en' }, { key: 's4', label: 'Deutsch', language: 'de' }]
    });
    const state = { plans: [], addresses: [], saves: {}, loads: [], appended: [], skips: [], tracks: [], changes: [], failures: 0, failFrom: options.failFrom ?? Infinity };
    const api = {
        items: async (assetId, following) => Array.from({ length: Math.min(following + 1, total - assetId + 1) }, (_, offset) => item(assetId + offset)),
        plan: async (assetId, audioKey) => {
            if (assetId >= state.failFrom) throw new Error('playback_capacity_exhausted');
            const sessionId = `session-${assetId}-${state.plans.length}`;
            state.plans.push({ assetId, audioKey, sessionId });
            return { sessionId, resumeSeconds: options.resume?.[assetId] ?? 0, durationSeconds: 2650 };
        },
        address: async sessionId => `https://jularr/api/v1/cast/${sessionId}/playlist.m3u8`,
        save: sessionId => (value, leaving) => { (state.saves[sessionId] ??= []).push({ ...value, leaving: !!leaving }); }
    };
    const sender = {
        load: async (entries, index) => { state.loads.push({ entries, index }); },
        append: async entry => { state.appended.push(entry); },
        skip: direction => state.skips.push(direction),
        setSubtitle: track => state.tracks.push(track),
        stop() {}
    };
    const choice = { assetId: 1, audioKey: '2', audioLanguage: 'de', subtitleKey: 's4', subtitleLanguage: 'de', subtitleForced: false };
    const cast = new queue.CastQueue(api, sender, { autoplay: options.autoplay ?? true, changed: (entry, index) => state.changes.push([entry.item.assetId, index]), failed: () => state.failures++ }, choice);
    const playing = (entryIndex, position, extra = {}) => ({ connected: true, paused: false, currentTime: position, duration: 2650, device: 'TV', volume: 0.5, muted: false, contentId: cast.entries[entryIndex].url, ...extra });
    return { cast, state, playing };
}

const last = (saves, sessionId) => saves[sessionId]?.at(-1);

test('Casting starts with the episode and the next three, each on a session of its own, in the viewer\'s audio and subtitle language', async () => {
    const { cast, state } = world();
    await cast.open(1, 120);

    assert.equal(cast.entries.length, 4);
    assert.deepEqual(plain(state.plans.map(plan => [plan.assetId, plan.audioKey])), [[1, '2'], [2, '2'], [3, '2'], [4, '2']]);
    assert.equal(new Set(cast.entries.map(entry => entry.sessionId)).size, 4);
    const sent = state.loads[0].entries;
    assert.deepEqual(plain(sent.map(entry => entry.subtitle)), ['Episode 1', 'Episode 2', 'Episode 3', 'Episode 4']);
    assert.equal(sent[0].startSeconds, 120);
    assert.deepEqual(plain(sent.map(entry => entry.activeTrack)), [1, 1, 1, 1]);
    assert.deepEqual(plain(sent[1].tracks.map(track => track.url)), [
        `${cast.entries[1].url.replace('playlist.m3u8', 'subtitles/s3')}`, `${cast.entries[1].url.replace('playlist.m3u8', 'subtitles/s4')}`]);
    assert.deepEqual(plain(sent[1].tracks.map(track => track.language)), ['en', 'de']);
    assert.equal(cast.index, 0);
    assert.deepEqual(state.changes, [[1, 0]]);
    // Only the item that plays now starts its time.
    assert.equal(last(state.saves, cast.entries[0].sessionId).isStarted, true);
    assert.equal(state.saves[cast.entries[1].sessionId], undefined);
});

test('A movie is a queue of one and a season\'s last episodes make a short queue', async () => {
    const movie = world({ total: 1 });
    await movie.cast.open(1);
    assert.equal(movie.cast.entries.length, 1);
    assert.equal(movie.cast.hasMore, false);
    const end = world({ total: 6 });
    await end.cast.open(5);
    assert.equal(end.cast.entries.length, 2);
    assert.equal(end.cast.hasMore, false);
});

test('Episodes the server has no room for are left out; the first must start', async () => {
    const { cast, state } = world({ failFrom: 3 });
    await cast.open(1);
    assert.equal(cast.entries.length, 2);
    assert.equal(state.loads[0].entries.length, 2);
    const refused = world({ failFrom: 1 });
    await assert.rejects(refused.cast.open(1), /capacity/);
    assert.equal(refused.state.loads.length, 0);
});

test('Where autoplay is off the device waits at the start of every episode after the first', async () => {
    const { cast, state } = world({ autoplay: false });
    await cast.open(1);
    assert.deepEqual(plain(state.loads[0].entries.map(entry => entry.autoplay)), [true, false, false, false]);
});

test('Another episode starts at its saved place', async () => {
    const { cast, state } = world({ resume: { 2: 600 } });
    await cast.open(1);
    assert.equal(state.loads[0].entries[1].startSeconds, 600);
});

test('When the device goes on, the finished episode counts as watched, the next one starts its time and the queue is filled again', async () => {
    const { cast, state, playing } = world();
    await cast.open(1);
    cast.update(playing(0, 1500));
    cast.update(playing(0, 2645));
    const first = cast.entries[0];
    cast.update(playing(1, 0));

    assert.equal(cast.index, 1);
    assert.deepEqual(last(state.saves, first.sessionId), { positionMs: 2650000, durationMs: 2650000, isEnded: true, isCompleted: true, leaving: true });
    assert.equal(last(state.saves, cast.entries[1].sessionId).isStarted, true);
    assert.deepEqual(state.changes.at(-1), [2, 1]);
    await cast.refill();
    assert.equal(cast.entries.length, 5);
    assert.equal(state.appended.length, 1);
    assert.equal(state.appended[0].subtitle, 'Episode 5');
    assert.equal(state.appended[0].autoplay, true);
    assert.equal(state.appended[0].activeTrack, 1);
    // Two more episodes remain in the season, so the queue fills up to three ahead.
    cast.update(playing(2, 0));
    await cast.refill();
    assert.equal(cast.entries.length, 6);
    cast.update(playing(3, 0));
    await cast.refill();
    assert.equal(cast.entries.length, 6);
    assert.equal(cast.hasMore, false);
    assert.equal(cast.canNext, true);
});

test('An episode left early keeps its place and stays open, so the viewer may come back', async () => {
    const { cast, state, playing } = world();
    await cast.open(1);
    cast.update(playing(0, 700));
    cast.update(playing(1, 0));
    const skipped = last(state.saves, cast.entries[0].sessionId);
    assert.deepEqual({ ...skipped }, { positionMs: 700000, durationMs: 2650000, isEnded: false, isCompleted: false, leaving: false });
    assert.equal(cast.entries[0].closed, false);
});

test('The place of the item that plays is saved on its own session, without ending anything', async () => {
    const { cast, state, playing } = world();
    await cast.open(1);
    cast.update(playing(0, 300));
    cast.update(playing(0, 345));
    cast.report();
    assert.deepEqual({ ...last(state.saves, cast.entries[0].sessionId) }, { positionMs: 345000, durationMs: 2650000, isEnded: false, isCompleted: false, leaving: false });
});

test('The last episode that ran out is finished when the device has nothing left to play', async () => {
    const { cast, state, playing } = world({ total: 2 });
    await cast.open(1);
    cast.update(playing(0, 2649));
    cast.update(playing(1, 0));
    cast.update(playing(1, 2648));
    cast.update({ ...playing(1, 2650), contentId: '' });
    assert.deepEqual({ ...last(state.saves, cast.entries[1].sessionId) }, { positionMs: 2650000, durationMs: 2650000, isEnded: true, isCompleted: true, leaving: true });
    const closed = cast.close();
    assert.equal(closed.finished, true);
    assert.equal(closed.assetId, 2);
});

test('Next and Previous move along the queue; before its first item the page builds a new queue', async () => {
    const { cast, state, playing } = world();
    await cast.open(1);
    assert.equal(cast.previous(), false);
    cast.next();
    cast.update(playing(0, 100));
    cast.update(playing(1, 0));
    assert.equal(cast.previous(), true);
    assert.deepEqual(state.skips, [1, -1]);
});

test('Going back to an episode that finished makes it again on a new session', async () => {
    const { cast, state, playing } = world();
    await cast.open(1);
    cast.update(playing(0, 2648));
    cast.update(playing(1, 0));
    const finished = cast.entries[0];
    assert.equal(finished.closed, true);
    cast.update(playing(0, 0));
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(state.loads.length, 2);
    assert.equal(cast.entries[0].item.assetId, 1);
    assert.notEqual(cast.entries[0].sessionId, finished.sessionId);
});

test('The subtitle on the device follows the viewer, and the episodes that follow take its language', async () => {
    const { cast, state, playing } = world();
    await cast.open(1);
    cast.setSubtitle('s3');
    assert.deepEqual(state.tracks, [0]);
    assert.equal(cast.current.activeSubtitle, 0);
    cast.setSubtitle(null);
    assert.deepEqual(state.tracks, [0, null]);
    cast.update(playing(0, 100));
    cast.update(playing(1, 0));
    await cast.refill();
    assert.equal(state.appended.at(-1).activeTrack, null);
    cast.setSubtitle('s3');
    cast.update(playing(2, 0));
    await cast.refill();
    assert.equal(state.appended.at(-1).activeTrack, 0);
});

test('Another audio track makes the item again at the same place and the next episodes follow its language', async () => {
    const { cast, state, playing } = world();
    await cast.open(1);
    cast.update(playing(0, 400));
    cast.update(playing(0, 420));
    const old = cast.entries.map(entry => entry.sessionId);
    await cast.setAudio('1');
    assert.equal(state.loads.length, 2);
    assert.deepEqual(state.plans.slice(4).map(plan => [plan.assetId, plan.audioKey]), [[1, '1'], [2, '1'], [3, '1'], [4, '1']]);
    assert.equal(state.loads[1].entries[0].startSeconds, 420);
    for (const id of old) assert.equal(last(state.saves, id).isEnded, true);
    assert.equal(last(state.saves, old[0]).positionMs, 420000);
    assert.equal(last(state.saves, old[1]).positionMs, 0);
});

test('Ending the cast ends every session; the item the device left is returned with its place', async () => {
    const { cast, state, playing } = world();
    await cast.open(1);
    cast.update(playing(0, 2648));
    cast.update(playing(1, 0));
    cast.update(playing(1, 900));
    const left = cast.close();
    assert.deepEqual({ ...left }, { assetId: 2, position: 900, finished: false });
    assert.equal(last(state.saves, cast.entries[0].sessionId).isCompleted, true);
    assert.deepEqual({ ...last(state.saves, cast.entries[1].sessionId) }, { positionMs: 900000, durationMs: 2650000, isEnded: true, isCompleted: false, leaving: true });
    for (const entry of cast.entries.slice(2)) assert.equal(last(state.saves, entry.sessionId).isEnded, true);
    assert.equal(last(state.saves, cast.entries[3].sessionId).isCompleted, false);
});
