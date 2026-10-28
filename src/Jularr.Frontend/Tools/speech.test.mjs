import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

function load(path) {
    const source = readFileSync(new URL(path, import.meta.url), 'utf8');
    const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const exports = {};
    runInNewContext(script, { exports, require: name => {
        if (name === './ModelStore') return load('../Modules/Speech/ModelStore.ts');
        if (name === './Runtime/NOTICE.generated.txt?url') return { default: '/notices.txt' };
        throw new Error('Unexpected speech test dependency: ' + name);
    } });
    return exports;
}
const { DeviceSession, chunkEnd } = load('../Modules/Speech/DeviceSession.ts');
const { preferenceFor, deviceVoice } = load('../Modules/Speech/Preferences.ts');

function session() {
    const messages = [], states = [], words = [];
    let cancellations = 0;
    const synth = { cancel() { cancellations++; }, speak(message) { messages.push(message); } };
    const session = new DeviceSession(synth, text => ({ text }), state => states.push(state), (...word) => words.push(word));
    return { session, messages, states, words, get cancellations() { return cancellations; } };
}
const options = { rate: 1.25, pitch: 1, volume: .8, language: 'de-DE' };
const item = text => ({ text, range: {} });

test('Speech pulls one current paragraph and queues only one bounded utterance', () => {
    const fixture = session();
    let pulled = 0;
    function* paragraphs() { pulled++; yield item('word '.repeat(1000)); pulled++; yield item('next'); }
    fixture.session.start(paragraphs(), options);
    assert.equal(pulled, 1);
    assert.equal(fixture.messages.length, 1);
    assert.ok(fixture.messages[0].text.length <= 240);
    assert.equal(fixture.messages[0].rate, 1.25);
    fixture.messages[0].onend();
    assert.equal(pulled, 1);
    assert.equal(fixture.messages.length, 2);
    assert.equal(fixture.session.state, 'speaking');
});

test('Stop invalidates completion and error callbacks from the old queue', () => {
    const fixture = session();
    fixture.session.start([item('first'), item('second')], options);
    const old = fixture.messages[0];
    fixture.session.stop();
    old.onend(); old.onerror(); old.onboundary({ charIndex: 0, charLength: 5 });
    assert.equal(fixture.messages.length, 1);
    assert.equal(fixture.session.state, 'idle');
    assert.equal(fixture.words.length, 0);
});

test('Pause cancels the platform queue and resumes at the last word without stale continuation', () => {
    const fixture = session();
    fixture.session.start([item('hello world, next sentence')], options);
    const old = fixture.messages[0];
    old.onstart(); old.onboundary({ charIndex: 6, charLength: 5 });
    fixture.session.pause();
    old.onend();
    assert.equal(fixture.session.state, 'paused');
    fixture.session.resume();
    assert.equal(fixture.messages[1].text, 'world, next sentence');
    assert.equal(fixture.session.state, 'speaking');
    old.onerror();
    assert.equal(fixture.session.state, 'speaking');
    fixture.messages[1].onend();
    assert.equal(fixture.session.state, 'idle');
});

test('Empty paragraphs are skipped and a device failure never speaks a later paragraph', () => {
    const fixture = session();
    fixture.session.start([item(' \n '), item('actual'), item('later')], options);
    assert.equal(fixture.messages[0].text, 'actual');
    fixture.messages[0].onerror();
    fixture.messages[0].onend();
    assert.equal(fixture.messages.length, 1);
    assert.equal(fixture.session.state, 'failed');
});

test('A newer read-aloud request owns the session even if the old platform sends its completion late', () => {
    const fixture = session();
    fixture.session.start([item('old')], options);
    const old = fixture.messages[0];
    fixture.session.start([item('new')], options);
    old.onend();
    assert.equal(fixture.messages.length, 2);
    assert.equal(fixture.messages[1].text, 'new');
});

test('Chunk boundaries retain exact offsets and never split a surrogate pair', () => {
    const text = 'a'.repeat(239) + '📖' + 'b'.repeat(800) + '. sentence';
    let start = 0, joined = '';
    while (start < text.length) {
        const end = chunkEnd(text, start);
        assert.ok(end > start && end - start <= 240);
        const chunk = text.slice(start, end);
        assert.ok(!/[\uD800-\uDBFF]$/.test(chunk));
        joined += chunk; start = end;
    }
    assert.equal(joined, text);
});

test('Speech preferences resolve exact locale then a compatible language, without overwriting stored choices', () => {
    const preferences = { voices: [{ language: 'de', voiceId: 'base' }, { language: 'de-AT', voiceId: 'specific' }] };
    assert.equal(preferenceFor(preferences, 'de-AT').voiceId, 'specific');
    assert.equal(preferenceFor(preferences, 'de-DE').voiceId, 'base');
    assert.equal(preferenceFor({ voices: [{ language: 'id-ID', voiceId: 'id' }] }, 'id').voiceId, 'id');
    assert.equal(preferenceFor(preferences, 'en'), undefined);
});

test('Device voice resolution honors only language-compatible choices and does not replace a missing preference', () => {
    const voices = [{ voiceURI: 'en', lang: 'en-US', default: true }, { voiceURI: 'de-b', lang: 'de-DE' }, { voiceURI: 'de-a', lang: 'de-AT' }];
    assert.equal(deviceVoice(voices, 'de-DE', 'en').voiceURI, 'de-b');
    assert.equal(deviceVoice(voices, 'de-DE', 'de-a').voiceURI, 'de-a');
    assert.equal(deviceVoice(voices, 'id-ID', null), undefined);
    assert.equal(voices[0].voiceURI, 'en');
});
