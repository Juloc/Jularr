// Playback capability probe (#403). Asks the browser what it can actually decode and deliver —
// MediaCapabilities first, canPlayType as the weaker fallback — and reports every claim as
// confirmed, inferred, unknown or unsupported. The server's PlaybackDecisionEngine makes the
// decision from this document; this file never decides how to play anything itself.
(() => {
    const schemaVersion = 1;
    // Bumped whenever the probe itself changes, so cached results are measured again.
    const probeVersion = 2;
    const cacheKey = "jularr.playback.capabilities";
    const maxAgeMs = 7 * 24 * 60 * 60 * 1000;

    // Representative codec strings: 1080p-class profiles/levels for 8- and 10-bit.
    const videoCodecs = {
        h264: { 8: "avc1.640028", 10: "avc1.6e0028" },
        hevc: { 8: "hvc1.1.6.L120.90", 10: "hvc1.2.4.L120.90" },
        av1: { 8: "av01.0.08M.08", 10: "av01.0.08M.10" },
        vp9: { 8: "vp09.00.40.08", 10: "vp09.02.40.10" },
        vp8: { 8: "vp8" }
    };
    const hev1 = "hev1.1.6.L120.90";
    const audioCodecs = {
        aac: "mp4a.40.2",
        mp3: "mp4a.6B",
        opus: "opus",
        flac: "flac",
        ac3: "ac-3",
        eac3: "ec-3",
        alac: "alac",
        vorbis: "vorbis",
        dts: "dtsc",
        truehd: "mlpa"
    };
    const containers = [
        { name: "mp4", mime: "video/mp4", video: ["h264", "hevc", "av1", "vp9"], audio: ["aac", "mp3", "opus", "flac", "ac3", "eac3", "alac", "dts", "truehd"] },
        { name: "webm", mime: "video/webm", video: ["vp9", "av1", "vp8"], audio: ["opus", "vorbis"] },
        { name: "matroska", mime: "video/x-matroska", video: ["h264", "hevc", "av1", "vp9"], audio: ["aac", "mp3", "opus", "flac", "ac3", "eac3", "vorbis"] }
    ];

    const rank = { unsupported: 0, unknown: 1, inferred: 2, confirmed: 3 };
    const strongest = (...values) => values.reduce((best, value) => rank[value] > rank[best] ? value : best, "unsupported");

    // canPlayType is a hint, never proof: "probably" is inferred, "maybe" unknown.
    const supportFromCanPlay = (answer) =>
        answer === "probably" ? "inferred" : answer === "maybe" ? "unknown" : "unsupported";

    // The Jularr build (a new server release may probe differently) as rendered by the page.
    const appVersion = (env) =>
        env.document?.documentElement?.dataset?.appVersion ||
        env.document?.querySelector?.("[data-app-version]")?.dataset?.appVersion ||
        "";

    // Browser engine + version and Jularr build: a change of any of them measures again.
    const fingerprint = (env) => {
        const brands = env.navigator.userAgentData?.brands?.map(brand => `${brand.brand}/${brand.version}`).join(",") || "";
        return `${schemaVersion}.${probeVersion}|${env.navigator.userAgent || ""}|${brands}|${appVersion(env)}`;
    };

    const media = (env, query) => {
        try {
            return env.window.matchMedia?.(query)?.matches === true;
        } catch {
            return false;
        }
    };

    const probe = async (env, element, contentType, extra = {}) => {
        const hinted = supportFromCanPlay(element.canPlayType(contentType));
        const capabilities = env.navigator.mediaCapabilities;
        if (!capabilities?.decodingInfo) {
            return { support: hinted };
        }

        try {
            const info = await capabilities.decodingInfo({
                type: "file",
                video: {
                    contentType,
                    width: extra.width || 1920,
                    height: extra.height || 1080,
                    bitrate: extra.bitrate || 8000000,
                    framerate: 24,
                    ...(extra.hdr || {})
                }
            });
            return {
                support: info.supported ? "confirmed" : "unsupported",
                smooth: info.smooth,
                powerEfficient: info.powerEfficient
            };
        } catch {
            // Unknown MIME types (e.g. video/x-matroska) throw; canPlayType is all we have.
            return { support: hinted };
        }
    };

    // MediaCapabilities only accepts audio MIME types for an audio configuration.
    const probeAudio = async (env, element, mime, codec) => {
        const contentType = `${mime.replace(/^video\//, "audio/")}; codecs="${codec}"`;
        const hinted = supportFromCanPlay(element.canPlayType(`${mime}; codecs="${codec}"`));
        const capabilities = env.navigator.mediaCapabilities;
        if (!capabilities?.decodingInfo) {
            return hinted;
        }

        try {
            const info = await capabilities.decodingInfo({
                type: "file",
                audio: { contentType, channels: "2", bitrate: 192000, samplerate: 48000 }
            });
            return info.supported ? "confirmed" : "unsupported";
        } catch {
            return hinted;
        }
    };

    const probeContainer = async (env, element, container) => {
        const video = [];
        for (const codec of container.video) {
            const strings = videoCodecs[codec];
            const eight = await probe(env, element, `${container.mime}; codecs="${strings[8]}"`);
            const ten = strings[10] ? await probe(env, element, `${container.mime}; codecs="${strings[10]}"`) : { support: "unsupported" };
            const support = strongest(eight.support, ten.support);
            const entry = { codec, support };
            if (rank[support] >= rank.inferred) {
                entry.bitDepths = [
                    ...(rank[eight.support] >= rank.inferred ? [8] : []),
                    ...(rank[ten.support] >= rank.inferred ? [10] : [])
                ];
                entry.smooth = eight.smooth ?? ten.smooth;
                entry.powerEfficient = eight.powerEfficient ?? ten.powerEfficient;
                const uhd = await probe(env, element, `${container.mime}; codecs="${strings[8]}"`, {
                    width: 3840,
                    height: 2160,
                    bitrate: 40000000
                });
                entry.maxHeight = uhd.support === "confirmed" ? 2160 : eight.support === "confirmed" ? 1080 : undefined;
                if (codec === "hevc" && container.name === "mp4" &&
                    supportFromCanPlay(element.canPlayType(`${container.mime}; codecs="${hev1}"`)) === "unsupported") {
                    // WebKit only accepts HEVC tagged hvc1; a remux retags it.
                    entry.codecTags = ["hvc1"];
                }
            }

            video.push(entry);
        }

        const audio = [];
        for (const codec of container.audio) {
            // Matroska uses the MP4 codec strings; WebM names its codecs plainly.
            const mimeCodec = container.name === "webm" ? codec : audioCodecs[codec];
            audio.push({ codec, support: await probeAudio(env, element, container.mime, mimeCodec) });
        }

        const containerSupport = strongest(
            supportFromCanPlay(element.canPlayType(container.mime)) === "unsupported" ? "unsupported" : "inferred",
            ...video.map(x => x.support));
        return { container: container.name, support: containerSupport, video, audio };
    };

    const probeHdr = async (env, element) => {
        const displayHigh = media(env, "(dynamic-range: high)") || media(env, "(video-dynamic-range: high)");
        const displayKnown = media(env, "(dynamic-range: standard)") || displayHigh;
        const hdrProbe = async (transferFunction, hdrMetadataType) => {
            const result = await probe(env, element, `video/mp4; codecs="${videoCodecs.hevc[10]}"`, {
                hdr: { transferFunction, colorGamut: "rec2020", ...(hdrMetadataType ? { hdrMetadataType } : {}) }
            });
            if (result.support === "unsupported") {
                // No HEVC Main 10: try the royalty-free 10-bit path browsers use for HDR.
                const vp9 = await probe(env, element, `video/mp4; codecs="${videoCodecs.vp9[10]}"`, {
                    hdr: { transferFunction, colorGamut: "rec2020", ...(hdrMetadataType ? { hdrMetadataType } : {}) }
                });
                if (vp9.support === "unsupported") {
                    return "unsupported";
                }
            }

            // Decoding works; the display decides whether it is real HDR or tone mapped.
            return displayHigh ? "confirmed" : "inferred";
        };

        const dolbyVision = supportFromCanPlay(element.canPlayType('video/mp4; codecs="dvh1.05.06"'));
        return {
            display: displayHigh ? "confirmed" : displayKnown ? "unsupported" : "unknown",
            hdr10: await hdrProbe("pq", "smpteSt2086"),
            hdr10Plus: await hdrProbe("pq", "smpteSt2094-40"),
            hlg: await hdrProbe("hlg"),
            dolbyVision: dolbyVision === "unsupported" ? "unsupported" : displayHigh ? dolbyVision : "unknown"
        };
    };

    const probeDelivery = (env, element) => {
        const hls = element.canPlayType("application/vnd.apple.mpegurl") ||
            element.canPlayType("application/x-mpegURL");
        const mediaSource = env.window.MediaSource || env.window.ManagedMediaSource;
        let fragmented = "unknown";
        try {
            fragmented = mediaSource?.isTypeSupported?.('video/mp4; codecs="avc1.640028,mp4a.40.2"') ? "confirmed" : mediaSource ? "unsupported" : "unknown";
        } catch {
            fragmented = "unknown";
        }

        const mp4 = supportFromCanPlay(element.canPlayType('video/mp4; codecs="avc1.640028,mp4a.40.2"'));
        // WebKit (Safari, every iOS browser, installed iOS PWAs) only plays MP4 over HTTP
        // with byte ranges, which a live stream cannot offer, so it gets native HLS. Other
        // engines play the live fragmented MP4 directly and need no HLS.
        const webKit = env.navigator.vendor === "Apple Computer, Inc." && typeof element.webkitSetPresentationMode === "function";
        return {
            // A native HLS answer is only ever "maybe"; any answer means the platform plays HLS.
            hls: hls === "" ? "unsupported" : webKit ? "inferred" : "unknown",
            progressiveMp4: webKit ? "unsupported" : rank[mp4] >= rank.inferred ? "inferred" : "unknown",
            mediaSource: mediaSource ? "confirmed" : "unsupported",
            fragmentedMp4: fragmented
        };
    };

    const presence = (value) => value ? "confirmed" : "unsupported";

    // What this page can do with one player stage and its <video>: the facts the player's presentation resolver
    // starts from (player-presentation.js). Element fullscreen needs a callable request on the stage AND the
    // document's matching enabled flag and state property (a bare method is not proof; iPhone WebKit exposes none
    // of them for elements). Native fullscreen is the WebKit-only video surface. Picture-in-picture lists every
    // usable route in the order the player tries them: the standard API first, then WebKit's presentation mode.
    const probePresentation = (doc, stage, video) => {
        const elementFullscreen =
            typeof stage?.requestFullscreen === "function" && doc.fullscreenEnabled === true && "fullscreenElement" in doc
                ? "standard"
                : typeof stage?.webkitRequestFullscreen === "function" && doc.webkitFullscreenEnabled === true && "webkitFullscreenElement" in doc
                    ? "webkit"
                    : null;
        const pictureInPicture = [];
        if (doc.pictureInPictureEnabled === true && typeof video.requestPictureInPicture === "function" && video.disablePictureInPicture !== true) {
            pictureInPicture.push("standard");
        }

        if (typeof video.webkitSetPresentationMode === "function" &&
            (typeof video.webkitSupportsPresentationMode !== "function" || video.webkitSupportsPresentationMode("picture-in-picture"))) {
            pictureInPicture.push("webkit");
        }

        return { elementFullscreen, nativeFullscreen: typeof video.webkitEnterFullscreen === "function", pictureInPicture };
    };

    const probeFeatures = (env, element) => {
        const presentation = probePresentation(env.document, env.document.documentElement, element);
        return {
            pictureInPicture: presence(presentation.pictureInPicture.length > 0),
            mediaSession: presence("mediaSession" in env.navigator),
            fullscreen: presence(presentation.elementFullscreen !== null || presentation.nativeFullscreen),
            orientationLock: typeof env.window.screen?.orientation?.lock === "function" ? "inferred" : "unsupported",
            wakeLock: presence("wakeLock" in env.navigator),
            // The web player renders one audio track per stream; switching needs a remux.
            audioTrackSelection: "unsupported"
        };
    };

    const clientKind = (env) =>
        media(env, "(display-mode: standalone)") || env.navigator.standalone === true ? "pwa" : "web";

    const clientName = (env) => {
        const brands = (env.navigator.userAgentData?.brands || []).filter(x => !/not.a.brand/i.test(x.brand));
        const brand = brands.find(x => x.brand !== "Chromium") || brands[0];
        return brand ? { name: brand.brand, version: brand.version } : { name: null, version: null };
    };

    const probeAll = async (env) => {
        const element = env.document.createElement("video");
        const result = [];
        for (const container of containers) {
            result.push(await probeContainer(env, element, container));
        }

        const name = clientName(env);
        return {
            schemaVersion,
            client: {
                kind: clientKind(env),
                name: name.name,
                version: name.version,
                appVersion: appVersion(env) || null
            },
            containers: result,
            hdr: await probeHdr(env, element),
            delivery: probeDelivery(env, element),
            // The web player draws text cues itself; styled ASS and bitmap subtitles it cannot.
            subtitles: { text: "confirmed", styledAss: "unsupported", image: "unsupported" },
            features: probeFeatures(env, element),
            display: {
                width: Math.round((env.window.screen?.width || 0) * (env.window.devicePixelRatio || 1)) || null,
                height: Math.round((env.window.screen?.height || 0) * (env.window.devicePixelRatio || 1)) || null,
                pixelRatio: env.window.devicePixelRatio || null
            }
        };
    };

    const readCache = (env, key) => {
        try {
            const cached = JSON.parse(env.storage.getItem(cacheKey) || "null");
            if (cached?.fingerprint === key && Date.now() - cached.createdAt < maxAgeMs && cached.capabilities) {
                return cached.capabilities;
            }
        } catch {
        }

        return null;
    };

    const writeCache = (env, key, capabilities) => {
        try {
            env.storage.setItem(cacheKey, JSON.stringify({ fingerprint: key, createdAt: Date.now(), capabilities }));
        } catch {
        }
    };

    const defaultEnv = () => ({
        window,
        navigator: window.navigator,
        document: window.document,
        storage: (() => {
            try {
                return window.localStorage;
            } catch {
                return { getItem: () => null, setItem: () => {}, removeItem: () => {} };
            }
        })()
    });

    let pending = null;

    // Cached per browser/app version; the display facts (HDR, standalone) are re-read every
    // time because they change with the monitor or the install state.
    const detect = async (env = defaultEnv()) => {
        const key = fingerprint(env);
        const cached = readCache(env, key);
        const capabilities = cached || await (pending ||= probeAll(env).finally(() => { pending = null; }));
        if (!cached) {
            writeCache(env, key, capabilities);
        }

        const displayHigh = media(env, "(dynamic-range: high)") || media(env, "(video-dynamic-range: high)");
        return {
            ...capabilities,
            client: { ...capabilities.client, kind: clientKind(env) },
            hdr: capabilities.hdr && {
                ...capabilities.hdr,
                display: displayHigh ? "confirmed" : capabilities.hdr.display === "confirmed" ? "unknown" : capabilities.hdr.display
            }
        };
    };

    // Connection hints only; measured throughput comes from the running player.
    const networkReport = (env = defaultEnv()) => {
        const connection = env.navigator.connection;
        return connection
            ? {
                saveData: connection.saveData === true,
                connectionType: typeof connection.type === "string" ? connection.type : null
            }
            : {};
    };

    const clear = (env = defaultEnv()) => {
        try {
            env.storage.removeItem(cacheKey);
        } catch {
        }
    };

    window.JularrPlaybackCapabilities = { detect, networkReport, clear, supportFromCanPlay, fingerprint, probePresentation, schemaVersion };
})();
