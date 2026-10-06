(() => {
    const DB_NAME = "jularr-review-v1";
  const DB_VERSION = 1;
  const ACTIVE_PROFILE_KEY = "jularr.activeProfile";

  const ratingValue = {
    Again: 1,
    Hard: 2,
    Good: 3,
    Easy: 4
  };

  function openDb() {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open(DB_NAME, DB_VERSION);
      request.onupgradeneeded = () => {
        const db = request.result;

        if (!db.objectStoreNames.contains("sessions")) {
          db.createObjectStore("sessions", { keyPath: "profileId" });
        }

        if (!db.objectStoreNames.contains("events")) {
          const events = db.createObjectStore("events", { keyPath: "key" });
          events.createIndex("profileId", "profileId", { unique: false });
        }
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }

  function requestResult(request) {
    return new Promise((resolve, reject) => {
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }

  async function withStore(storeName, mode, action) {
    const db = await openDb();
    try {
      const transaction = db.transaction(storeName, mode);
      const store = transaction.objectStore(storeName);
      const result = await action(store);
      await new Promise((resolve, reject) => {
        transaction.oncomplete = resolve;
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error);
      });
      return result;
    } finally {
      db.close();
    }
  }

  async function saveSession(profileId, cards) {
    if (!profileId || !Array.isArray(cards)) {
      return;
    }

    await withStore("sessions", "readwrite", store =>
      requestResult(store.put({
        profileId,
        cards,
        savedAtUtc: new Date().toISOString()
      }))
    );
  }

  async function loadSession(profileId) {
    if (!profileId) {
      return null;
    }

    return withStore("sessions", "readonly", store =>
      requestResult(store.get(profileId))
    );
  }

  async function queueEvent(profileId, cardId, rating, reviewedAtUtc = new Date().toISOString()) {
    if (!profileId || !cardId || !ratingValue[rating]) {
      throw new Error("Invalid offline review event.");
    }

    const eventId = crypto.randomUUID();
    const event = {
      key: profileId + ":" + eventId,
      profileId,
      eventId,
      cardId,
      rating: ratingValue[rating],
      reviewedAtUtc
    };

    await withStore("events", "readwrite", store =>
      requestResult(store.put(event))
    );

    return event;
  }

  async function listEvents(profileId) {
    if (!profileId) {
      return [];
    }

    return withStore("events", "readonly", async store => {
      const index = store.index("profileId");
      const rows = await requestResult(index.getAll(profileId));
      return rows.sort((a, b) =>
        a.reviewedAtUtc.localeCompare(b.reviewedAtUtc)
        || a.eventId.localeCompare(b.eventId));
    });
  }

  async function removeEvents(profileId, ids) {
    if (!profileId || !ids?.length) {
      return;
    }

    const unique = [...new Set(ids)];
    await withStore("events", "readwrite", async store => {
      for (const id of unique) {
        await requestResult(store.delete(profileId + ":" + id));
      }
    });
  }

  async function sync(profileId, syncUrl, antiForgeryToken) {
    const batchSize = 100;
    let synced = 0;
    let rejectedCount = 0;

    while (true) {
      const events = (await listEvents(profileId)).slice(0, batchSize);
      if (!events.length) {
        return { synced, rejected: rejectedCount };
      }

      const response = await fetch(syncUrl, {
        method: "POST",
        credentials: "same-origin",
        headers: {
          "Content-Type": "application/json",
          "RequestVerificationToken": antiForgeryToken
        },
        body: JSON.stringify({
          events: events.map(event => ({
            eventId: event.eventId,
            // Events queued before directional cards carry only the term ID;
            // the server resolves those to the word's Recognition card.
            cardId: event.cardId ?? null,
            termId: event.termId ?? null,
            rating: event.rating,
            reviewedAtUtc: event.reviewedAtUtc
          }))
        })
      });

      if (!response.ok) {
        throw new Error("Offline review sync failed.");
      }

      const result = await response.json();
      const accepted = result.accepted ?? [];
      const alreadyApplied = result.alreadyApplied ?? [];
      const rejected = result.rejected ?? [];
      const completed = [
        ...accepted,
        ...alreadyApplied,
        ...rejected
      ];

      if (!completed.length) {
        throw new Error("Offline review sync made no progress.");
      }

      await removeEvents(profileId, completed);
      synced += accepted.length + alreadyApplied.length;
      rejectedCount += rejected.length;
    }
  }

  function setActiveProfile(profileId) {
    if (profileId) {
      localStorage.setItem(ACTIVE_PROFILE_KEY, profileId);
    }
  }

  function getActiveProfile() {
    return localStorage.getItem(ACTIVE_PROFILE_KEY);
  }

  function clearActiveProfile() {
    localStorage.removeItem(ACTIVE_PROFILE_KEY);
  }

  function contextSource(card) {
    const context = card?.context;
    if (!context) {
      return "";
    }

    const totalSeconds = Math.max(0, Math.floor((context.cueStartMs ?? 0) / 1000));
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = String(totalSeconds % 60).padStart(2, "0");
    return [
      context.animeTitle,
      "S" + String(context.seasonNumber).padStart(2, "0"),
      "E" + String(context.episodeNumber).padStart(2, "0"),
      minutes + ":" + seconds
    ].join(" · ");
  }

  async function renderOfflinePage() {
    const root = document.querySelector("[data-offline-review-page]");
    if (!root) {
      return;
    }

    const profileId = getActiveProfile();
    if (!profileId) {
      return;
    }

    const session = await loadSession(profileId);
    if (!session?.cards?.length) {
      return;
    }

    const pending = await listEvents(profileId);
    const pendingCards = new Set(pending.map(event => event.cardId).filter(Boolean));
    const cards = session.cards.filter(card =>
      card.cardId && !pendingCards.has(card.cardId));
    const card = cards[0];

    const empty = root.querySelector("[data-offline-empty]");
    const review = root.querySelector("[data-offline-card]");

    if (!card) {
      if (empty) {
        empty.innerHTML = "<h1>Offline reviews saved</h1><p>Your loaded batch is finished. Reconnect to sync the queued ratings and refresh the schedule.</p>";
      }
      return;
    }

    empty?.setAttribute("hidden", "");
    review?.removeAttribute("hidden");

    const term = root.querySelector("[data-offline-term]");
    term.textContent = card.prompt ?? "";
    term.lang = card.promptLanguage ?? "";
    const reading = root.querySelector("[data-offline-reading]");
    reading.textContent = card.promptReading ?? "";
    reading.lang = card.promptLanguage ?? "";
    const answer = root.querySelector("[data-offline-meaning]");
    answer.textContent =
      [card.answer, card.answerReading].filter(Boolean).join(" · ")
      || "No local dictionary meaning available.";
    answer.lang = card.answerLanguage ?? "";

    const context = root.querySelector("[data-offline-context]");
    if (card.context) {
      context?.removeAttribute("hidden");
      root.querySelector("[data-offline-sentence]").textContent = card.context.sentence ?? "";
      root.querySelector("[data-offline-source]").textContent = contextSource(card);
    } else {
      context?.setAttribute("hidden", "");
    }

    for (const button of root.querySelectorAll("[data-offline-rating]")) {
      const rating = button.dataset.offlineRating;
      button.onclick = async () => {
        button.disabled = true;
        try {
          await queueEvent(profileId, card.cardId, rating);
          await renderOfflinePage();
        } finally {
          button.disabled = false;
        }
      };
    }
  }

  window.JularrOfflineReviews = {
    saveSession,
    loadSession,
    queueEvent,
    listEvents,
    removeEvents,
    sync,
    setActiveProfile,
    getActiveProfile,
    clearActiveProfile
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => void renderOfflinePage());
  } else {
    void renderOfflinePage();
  }

  window.addEventListener("online", () => {
    if (document.querySelector("[data-offline-review-page]")) {
      location.href = "/Learn";
    }
  });
})();
