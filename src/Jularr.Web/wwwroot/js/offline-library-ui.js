(() => {
  "use strict";

  /**
   * DOM wiring for the offline library (#221 part 1/2): the reusable
   * "Save offline" action (Pages/Shared/_OfflineLibraryAction.cshtml), the
   * Settings → Offline page, the shared personal download preview
   * and the Library "Offline" filter (Pages/Books/Index.cshtml,
   * Pages/Novels/Index.cshtml). Pure decisions and I/O live in
   * offline-library.js / offline-library-storage.js / offline-library-manager.js;
   * this file only reads/writes the DOM.
   *
   * Text is read from the UI catalog (offlineLibrary.* keys,
   * UiTranslationResources.cs) through a JSON script element rendered by
   * _Layout.cshtml, the same pattern pwa.js's shellText already uses for
   * pwa.* keys.
   */

  const catalog = (() => {
    try {
      return JSON.parse(document.getElementById("offline-library-text")?.textContent || "{}");
    } catch {
      return {};
    }
  })();
  const text = (key) => catalog[key] || key;
  const format = (key, replacements) =>
    Object.entries(replacements).reduce(
      (result, [name, value]) => result.replaceAll(`{${name}}`, String(value)),
      text(key));

  const manager = () => window.JularrOfflineLibraryManager.getSharedManager();

  const STATE_LABEL_KEYS = {
    unavailable: "offlineLibrary.action.unavailable",
    idle: "offlineLibrary.action.idle",
    downloading: "offlineLibrary.action.downloading",
    paused: "offlineLibrary.action.paused",
    failed: "offlineLibrary.action.failed",
    available: "offlineLibrary.action.available",
    "update-available": "offlineLibrary.action.updateAvailable"
  };
  const stateLabel = (state) => STATE_LABEL_KEYS[state] ? text(STATE_LABEL_KEYS[state]) : state;

  const bookState = async (instance, workId) => {
    const books = await instance.listBooks();
    const record = books.find((b) => b.workId === workId);
    return record?.status || "idle";
  };

  const renderAction = async (root) => {
    const workId = root.dataset.offlineSave;
    const button = root.querySelector("[data-offline-save-button]");
    const status = root.querySelector("[data-offline-save-status]");
    if (!button || !status) return;

    let instance;
    try {
      instance = await manager();
    } catch {
      status.textContent = text("offlineLibrary.action.unavailable");
      button.disabled = true;
      return;
    }

    const refresh = async () => {
      const state = await bookState(instance, workId);
      button.disabled = false;
      status.textContent = stateLabel(state);
      button.textContent = state === "idle" || state === "failed"
        ? text("offlineLibrary.action.saveButton")
        : state === "downloading"
          ? text("offlineLibrary.action.pauseButton")
          : state === "paused"
            ? text("offlineLibrary.action.resumeButton")
            : text("offlineLibrary.action.checkUpdateButton");
      button.dataset.offlineState = state;
    };

    button.addEventListener("click", async () => {
      button.disabled = true;
      try {
        const state = button.dataset.offlineState;
        if (state === "downloading") {
          await instance.pause(workId);
        } else if (state === "paused") {
          await instance.resume(workId);
        } else if (state === "failed") {
          await instance.retryFailed(workId);
        } else {
          await instance.enqueueBook(workId);
          await instance.processQueue(workId);
        }
      } catch {
        status.textContent = text("offlineLibrary.action.saveFailed");
      } finally {
        button.disabled = false;
        await refresh();
      }
    });

    instance.onChange(() => { void refresh(); });
    await refresh();
  };

  const renderSettingsPage = async (root) => {
    const list = root.querySelector("[data-offline-list]");
    const usage = root.querySelector("[data-offline-usage]");
    const degradedNotice = root.querySelector("[data-offline-degraded]");
    const wifiToggle = root.querySelector("[data-offline-wifi-only]");
    if (!list || !usage) return;

    let instance;
    try {
      instance = await manager();
    } catch {
      list.textContent = text("offlineLibrary.settings.signInRequired");
      return;
    }

    if (degradedNotice) {
      degradedNotice.hidden = !instance.isDegraded;
    }

    if (wifiToggle) {
      wifiToggle.checked = await instance.getWifiOnly();
      wifiToggle.addEventListener("change", () => {
        void instance.setWifiOnly(wifiToggle.checked);
      });
    }

    const refresh = async () => {
      const books = await instance.listBooks();
      const usageInfo = await instance.storageUsage();
      usage.textContent = format("offlineLibrary.settings.storageUsed", { formatted: usageInfo.formatted })
        + (usageInfo.persisted ? "" : text("offlineLibrary.settings.notPersistedSuffix"));

      list.replaceChildren();
      if (books.length === 0) {
        const empty = document.createElement("p");
        empty.className = "muted";
        empty.textContent = text("offlineLibrary.settings.empty");
        list.appendChild(empty);
        return;
      }

      for (const book of books) {
        const row = document.createElement("div");
        row.className = "offline-library-row";

        const title = document.createElement("strong");
        title.textContent = book.manifest.title;
        row.appendChild(title);

        const state = document.createElement("span");
        state.textContent = stateLabel(book.status);
        row.appendChild(state);

        const removeButton = document.createElement("button");
        removeButton.type = "button";
        removeButton.className = "button";
        removeButton.textContent = text("offlineLibrary.settings.remove");
        removeButton.addEventListener("click", async () => {
          removeButton.disabled = true;
          await instance.removeBook(book.workId);
        });
        row.appendChild(removeButton);

        list.appendChild(row);
      }
    };

    instance.onChange(() => { void refresh(); });
    await refresh();
  };

  // Offline state belongs to the signed-in profile's browser storage, not the server acquisition queue.
  const renderDownloadPreview = async (root) => {
    const list = root.querySelector("[data-offline-preview-list]");
    const loading = root.querySelector("[data-offline-preview-loading]");
    const error = root.querySelector("[data-offline-preview-error]");
    const badge = document.querySelector("[data-offline-download-count]");
    let instance;
    let revision = 0;

    const refresh = async () => {
      const current = ++revision;
      const open = root.matches(":popover-open");
      loading.hidden = !open;
      error.hidden = true;
      try {
        instance ||= await manager();
        const [books, media] = await Promise.all([instance.listBooks(), window.JularrOfflineMediaManager?.packages?.() || []]);
        if (current !== revision) return;
        const records = [
          ...books.map(book => ({ title: book.manifest.title, state: book.status, updatedAt: book.updatedAt })),
          ...media.map(pkg => ({ title: pkg.title, state: pkg.state === "ready" ? "available" : pkg.state, updatedAt: pkg.updatedAt, package: pkg }))
        ];
        const active = records.filter(item => item.state === "downloading").length;
        if (badge) {
          badge.hidden = active === 0;
          badge.textContent = active > 99 ? "99+" : String(active);
        }
        if (!open || !root.matches(":popover-open")) return;
        list.replaceChildren();
        records.sort((left, right) => Number(right.state === "downloading") - Number(left.state === "downloading")
          || new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime());
        if (!records.length) {
          const empty = document.createElement("p");
          empty.className = "muted";
          empty.textContent = text("offlineLibrary.settings.empty");
          list.append(empty);
        }
        for (const item of records.slice(0, 6)) {
          const row = document.createElement("article");
          row.className = "header-preview-item";
          row.append(root.querySelector("[data-offline-preview-icon]").content.cloneNode(true));
          const content = document.createElement("div");
          const title = document.createElement("a");
          title.href = "/Settings/Offline";
          title.textContent = item.title;
          title.title = item.title;
          const state = document.createElement("span");
          state.className = "header-preview-meta";
          state.textContent = stateLabel(item.state);
          content.append(title, state);
          const updatedAt = new Date(item.updatedAt);
          if (item.updatedAt != null && !Number.isNaN(updatedAt.getTime())) {
            const time = document.createElement("time");
            time.dateTime = updatedAt.toISOString();
            time.textContent = updatedAt.toLocaleString(document.documentElement.lang);
            content.append(time);
          }
          if (item.package?.sizeBytes > 0 && item.state === "downloading") {
            const progress = document.createElement("progress");
            progress.max = item.package.sizeBytes;
            progress.value = mediaDownloadedBytes(item.package);
            progress.setAttribute("aria-label", item.title);
            content.append(progress);
          }
          row.append(content);
          list.append(row);
        }
      } catch {
        if (current === revision && root.matches(":popover-open")) {
          list.replaceChildren();
          error.hidden = false;
        }
      } finally {
        if (current === revision) {
          loading.hidden = true;
          root.dispatchEvent(new Event("jularr:header-preview-updated"));
        }
      }
    };

    root.addEventListener("toggle", event => {
      if (event.newState === "open") void refresh();
    });
    ["jularr:offline-media-progress", "jularr:offline-media-ready", "jularr:offline-media-removed"].forEach(name => {
      window.addEventListener(name, () => { void refresh(); });
    });
    try {
      instance = await manager();
      instance.onChange(() => { void refresh(); });
      await refresh();
    } catch {
      loading.hidden = true;
    }
  };

  const mediaDownloadedBytes = pkg => (pkg.resources || []).reduce((total, resource) => {
    const core = window.JularrOfflineMedia;
    const chunks = Math.min(resource.completedChunks || 0, core?.chunkCount(resource.sizeBytes || 0) || 0);
    return total + Math.min(resource.sizeBytes || 0, chunks * (core?.CHUNK_BYTES || 0));
  }, 0);

  /**
   * Library "Offline" filter (issue #221's UX section): purely a client-side
   * show/hide over server-rendered cards, since what is downloaded is
   * profile-and-device-local browser state the server never sees. Cards
   * carry `data-work-id`; filter buttons carry
   * `data-library-filter-option="all"|"offline"`.
   */
  const renderLibraryFilter = async (root) => {
    const cards = Array.from(root.querySelectorAll("[data-work-id]"));
    const options = Array.from(root.querySelectorAll("[data-library-filter-option]"));
    if (cards.length === 0 || options.length === 0) return;

    let instance;
    try {
      instance = await manager();
    } catch {
      return;
    }

    let offlineWorkIds = new Set();
    const applyFilter = (filter) => {
      for (const card of cards) {
        // A class, not the hidden attribute, so page-level filters (tabs, genre, search) combine with it.
        card.classList.toggle("is-offline-hidden", filter === "offline" && !offlineWorkIds.has(card.dataset.workId));
      }
    };

    const currentFilter = () =>
      options.find((option) => option.classList.contains("active"))?.dataset.libraryFilterOption || "all";

    const refresh = async () => {
      const books = await instance.listBooks();
      offlineWorkIds = new Set(
        books.filter((b) => b.status === "available").map((b) => b.workId));
      applyFilter(currentFilter());
    };

    options.forEach((option) => {
      option.addEventListener("click", () => {
        options.forEach((other) => other.classList.toggle("active", other === option));
        applyFilter(option.dataset.libraryFilterOption);
      });
    });

    instance.onChange(() => { void refresh(); });
    await refresh();
  };

  const initialize = () => {
    document.querySelectorAll("[data-offline-save]").forEach((root) => { void renderAction(root); });
    document.querySelectorAll("[data-offline-settings-page]").forEach((root) => { void renderSettingsPage(root); });
    document.querySelectorAll("[data-offline-download-preview]").forEach((root) => { void renderDownloadPreview(root); });
    document.querySelectorAll("[data-offline-library-filter]").forEach((root) => { void renderLibraryFilter(root); });
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initialize, { once: true });
  } else {
    initialize();
  }
})();
