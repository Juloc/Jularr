(() => {
  "use strict";

  /**
   * Browser storage adapter for the offline library (#221 part 1). Pure
   * decisions live in offline-library.js (window.JularrOfflineLibrary); this
   * file only talks to IndexedDB, OPFS and the network, so it is not unit
   * tested the same way (mirrors reader-tts.js next to tts.js).
   *
   * Ownership, matching docs/OFFLINE_LIBRARY.md:
   * - IndexedDB: manifests, download queue/state, verified chapter hashes,
   *   bookmarks/progress sync queue, local settings (Wi-Fi-only).
   * - OPFS: chapter payload JSON and image/cover bytes, when available.
   *   Falls back to storing chapter JSON in IndexedDB ("degraded mode") when
   *   OPFS or persistent storage cannot be used; images are skipped in that
   *   mode rather than exhausting IndexedDB with binary blobs.
   * - Everything is namespaced by the signed-in profile id (data-profile-id on
   *   <body>, rendered by Pages/Shared/_Layout.cshtml). No profile id means no
   *   database is opened at all, so logging out hides every account's offline
   *   state without deleting it (an explicit Settings → Offline action removes
   *   a book's local files permanently).
   */

  const engine = window.JularrOfflineLibrary;
  const DB_VERSION = 1;
  const STORES = {
    manifests: "manifests",
    queue: "queue",
    verified: "verified",
    settings: "settings",
    syncQueue: "syncQueue"
  };

  const currentProfileId = () => document.body?.dataset?.profileId || "";

    const dbNameFor = (profileId) => `jularr-offline-library:${profileId}`;

  const openDatabase = (profileId) => new Promise((resolve, reject) => {
    if (!profileId) {
      reject(new Error("No signed-in profile; offline storage is unavailable."));
      return;
    }

    const request = indexedDB.open(dbNameFor(profileId), DB_VERSION);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(STORES.manifests)) {
        db.createObjectStore(STORES.manifests, { keyPath: "workId" });
      }
      if (!db.objectStoreNames.contains(STORES.queue)) {
        const store = db.createObjectStore(STORES.queue, { keyPath: "id" });
        store.createIndex("workId", "workId", { unique: false });
      }
      if (!db.objectStoreNames.contains(STORES.verified)) {
        const store = db.createObjectStore(STORES.verified, { keyPath: "id" });
        store.createIndex("workId", "workId", { unique: false });
      }
      if (!db.objectStoreNames.contains(STORES.settings)) {
        db.createObjectStore(STORES.settings, { keyPath: "key" });
      }
      if (!db.objectStoreNames.contains(STORES.syncQueue)) {
        db.createObjectStore(STORES.syncQueue, { keyPath: "clientEventId" });
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });

  const tx = (db, storeNames, mode, work) => new Promise((resolve, reject) => {
    const transaction = db.transaction(storeNames, mode);
    const stores = (Array.isArray(storeNames) ? storeNames : [storeNames])
      .map((name) => transaction.objectStore(name));
    let result;
    Promise.resolve(work(...stores))
      .then((value) => { result = value; })
      .catch(reject);
    transaction.oncomplete = () => resolve(result);
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error || new Error("Transaction aborted."));
  });

  const requestToPromise = (request) => new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });

  // --- OPFS (with IndexedDB degraded-mode fallback) ---------------------

  let opfsRootPromise = null;
  const opfsSupported = () => typeof navigator.storage?.getDirectory === "function";

  const opfsRoot = async () => {
    if (!opfsSupported()) return null;
    opfsRootPromise ??= navigator.storage.getDirectory().catch(() => null);
    return opfsRootPromise;
  };

  const opfsDirectory = async (segments, { create }) => {
    let dir = await opfsRoot();
    if (!dir) return null;

    for (const segment of segments) {
      try {
        dir = await dir.getDirectoryHandle(segment, { create });
      } catch {
        return null;
      }
    }

    return dir;
  };

  // OPFS is shared by the browser origin, unlike our IndexedDB databases.
  // Keep the profile in every path as well, otherwise two profiles that own a
  // work with the same id could read each other's downloaded text or images.
  const chapterPath = (profileId, workId) => ["offline", profileId, workId, "chapters"];
  const assetPath = (profileId, workId, volumeId) => ["offline", profileId, workId, "assets", volumeId];

  /** Requests persistent storage (best-effort) and reports whether OPFS is usable. */
  const requestPersistence = async () => {
    let persisted = false;
    try {
      persisted = await navigator.storage?.persist?.() ?? false;
    } catch {
      persisted = false;
    }

    return { persisted, opfsAvailable: opfsSupported() };
  };

  const writeOpfsFile = async (segments, fileName, dataOrText) => {
    const dir = await opfsDirectory(segments, { create: true });
    if (!dir) return false;

    try {
      const handle = await dir.getFileHandle(fileName, { create: true });
      const writable = await handle.createWritable();
      await writable.write(dataOrText);
      await writable.close();
      return true;
    } catch {
      return false;
    }
  };

  const readOpfsFile = async (segments, fileName, { asText }) => {
    const dir = await opfsDirectory(segments, { create: false });
    if (!dir) return null;

    try {
      const handle = await dir.getFileHandle(fileName, { create: false });
      const file = await handle.getFile();
      return asText ? await file.text() : await file.arrayBuffer();
    } catch {
      return null;
    }
  };

  const deleteOpfsEntry = async (segments, fileName) => {
    const dir = await opfsDirectory(segments, { create: false });
    if (!dir) return;

    try {
      await dir.removeEntry(fileName);
    } catch {
      // Already gone, or never existed.
    }
  };

  const removeOpfsWork = async (profileId, workId) => {
    const parent = await opfsDirectory(["offline", profileId], { create: false });
    if (!parent) return;

    try {
      await parent.removeEntry(workId, { recursive: true });
    } catch {
      // Already gone.
    }
  };

  const removeOpfsProfile = async (profileId) => {
    const parent = await opfsDirectory(["offline"], { create: false });
    if (!parent) return;
    try {
      await parent.removeEntry(profileId, { recursive: true });
    } catch {
      // Already gone, or OPFS cleanup is unavailable.
    }
  };

  // --- Public store, namespaced to the current profile -------------------

  /**
   * Opens the current profile's offline store. Throws when no profile is
   * signed in; callers (the UI layer) must treat that as "offline downloads
   * are unavailable while signed out" rather than a hard error.
   */
  const openStoreForProfile = async (profileId) => {
    const db = await openDatabase(profileId);
    const persistence = await requestPersistence();

    const getManifest = async (workId) =>
      tx(db, STORES.manifests, "readonly", (store) => requestToPromise(store.get(workId)));

    const putManifest = async (record) =>
      tx(db, STORES.manifests, "readwrite", (store) => requestToPromise(store.put(record)));

    const deleteManifest = async (workId) =>
      tx(db, STORES.manifests, "readwrite", (store) => requestToPromise(store.delete(workId)));

    const listManifests = async () =>
      tx(db, STORES.manifests, "readonly", (store) => requestToPromise(store.getAll()));

    const listQueue = async (workId) =>
      tx(db, STORES.queue, "readonly", (store) => {
        const index = store.index("workId");
        return requestToPromise(index.getAll(IDBKeyRange.only(workId)));
      });

    const putQueueItem = async (item) =>
      tx(db, STORES.queue, "readwrite", (store) => requestToPromise(store.put(item)));

    const deleteQueueItem = async (id) =>
      tx(db, STORES.queue, "readwrite", (store) => requestToPromise(store.delete(id)));

    const clearQueueForWork = async (workId) =>
      tx(db, [STORES.queue], "readwrite", async (store) => {
        const index = store.index("workId");
        const items = await requestToPromise(index.getAll(IDBKeyRange.only(workId)));
        await Promise.all(items.map((item) => requestToPromise(store.delete(item.id))));
      });

    const listVerified = async (workId) =>
      tx(db, STORES.verified, "readonly", (store) => {
        const index = store.index("workId");
        return requestToPromise(index.getAll(IDBKeyRange.only(workId)));
      });

    const putVerified = async (record) =>
      tx(db, STORES.verified, "readwrite", (store) => requestToPromise(store.put(record)));

    const deleteVerified = async (id) =>
      tx(db, STORES.verified, "readwrite", (store) => requestToPromise(store.delete(id)));

    const clearVerifiedForWork = async (workId) =>
      tx(db, [STORES.verified], "readwrite", async (store) => {
        const index = store.index("workId");
        const items = await requestToPromise(index.getAll(IDBKeyRange.only(workId)));
        await Promise.all(items.map((item) => requestToPromise(store.delete(item.id))));
      });

    const getSetting = async (key, fallback) => {
      const row = await tx(db, STORES.settings, "readonly", (store) => requestToPromise(store.get(key)));
      return row ? row.value : fallback;
    };

    const putSetting = async (key, value) =>
      tx(db, STORES.settings, "readwrite", (store) => requestToPromise(store.put({ key, value })));

    const listSyncQueue = async () =>
      tx(db, STORES.syncQueue, "readonly", (store) => requestToPromise(store.getAll()));

    const putSyncEvent = async (event) =>
      tx(db, STORES.syncQueue, "readwrite", (store) => requestToPromise(store.put(event)));

    const deleteSyncEvent = async (clientEventId) =>
      tx(db, STORES.syncQueue, "readwrite", (store) => requestToPromise(store.delete(clientEventId)));

    const saveChapterPayload = async (workId, chapterId, payload) => {
      const text = JSON.stringify(payload);
      if (persistence.opfsAvailable && await writeOpfsFile(chapterPath(profileId, workId), `${chapterId}.json`, text)) {
        return { degraded: false };
      }

      // Degraded mode: keep the chapter payload in IndexedDB so the download
      // can still finish and be read offline, just without OPFS's larger quota.
      await tx(db, STORES.manifests, "readwrite", async () => {});
      await putSetting(`chapter:${workId}:${chapterId}`, text);
      return { degraded: true };
    };

    const loadChapterPayload = async (workId, chapterId) => {
      const fromOpfs = await readOpfsFile(chapterPath(profileId, workId), `${chapterId}.json`, { asText: true });
      const text = fromOpfs ?? await getSetting(`chapter:${workId}:${chapterId}`, null);
      return text ? JSON.parse(text) : null;
    };

    const deleteChapterPayload = async (workId, chapterId) => {
      await deleteOpfsEntry(chapterPath(profileId, workId), `${chapterId}.json`);
      await tx(db, STORES.settings, "readwrite", (store) => requestToPromise(
        store.delete(`chapter:${workId}:${chapterId}`)));
    };

    const saveAsset = async (workId, volumeId, assetName, bytes) => {
      if (!persistence.opfsAvailable) return { degraded: true };
      const ok = await writeOpfsFile(assetPath(profileId, workId, volumeId), assetName, bytes);
      return { degraded: !ok };
    };

    const loadAsset = async (workId, volumeId, assetName) =>
      readOpfsFile(assetPath(profileId, workId, volumeId), assetName, { asText: false });

    const removeWork = async (workId) => {
      await deleteManifest(workId);
      await clearQueueForWork(workId);
      await clearVerifiedForWork(workId);
      await removeOpfsWork(profileId, workId);

      // Sweep any degraded-mode chapter payloads kept in IndexedDB settings.
      const all = await tx(db, STORES.settings, "readonly", (store) => requestToPromise(store.getAll()));
      const prefix = `chapter:${workId}:`;
      await tx(db, STORES.settings, "readwrite", (store) => Promise.all(
        all.filter((row) => row.key.startsWith(prefix)).map((row) => requestToPromise(store.delete(row.key)))));
    };

    const clearProfile = async () => {
      await tx(db, Object.values(STORES), "readwrite", (...stores) => {
        stores.forEach(store => store.clear());
      });
      await removeOpfsProfile(profileId);
    };

    return {
      profileId,
      isDegraded: !persistence.opfsAvailable,
      persisted: persistence.persisted,
      getManifest, putManifest, deleteManifest, listManifests,
      listQueue, putQueueItem, deleteQueueItem, clearQueueForWork,
      listVerified, putVerified, deleteVerified, clearVerifiedForWork,
      getSetting, putSetting,
      listSyncQueue, putSyncEvent, deleteSyncEvent,
      saveChapterPayload, loadChapterPayload, deleteChapterPayload,
      saveAsset, loadAsset,
      removeWork, clearProfile
    };
  };

  const openStore = () => openStoreForProfile(currentProfileId());

  window.JularrOfflineLibraryStorage = Object.freeze({
    openStore,
    openStoreForProfile,
    currentProfileId,
    requestPersistence
  });
})();
