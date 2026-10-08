/* The Home/Discover layout editor (Pages/Shared/_HomeLayoutEditor.cshtml). The order is the DOM order of the cards: every card holds a hidden
   "order" input, so the form posts what the viewer sees. A card moves by dragging its handle, by the arrow buttons, or from the keyboard and a
   remote: Enter on the handle picks the card up, Up/Down move it, Enter or Escape drops it. */
(() => {
    "use strict";

    const fill = (template, values) => (template || "").replace(/\{(\w+)\}/g, (match, key) => (key in values ? values[key] : match));

    const init = root => {
        const list = root.querySelector(".home-layout-list");
        const status = root.querySelector("[data-home-status]");
        const preview = root.querySelector("[data-home-preview]");
        let moving = false;
        // A card the instance does not serve (Setup hides it while its module switch is off) is not part of the order the viewer moves and sees.
        const items = () => Array.from(list.querySelectorAll("[data-home-item]")).filter(item => !item.hidden);
        const announce = text => { if (status) { status.textContent = ""; status.textContent = text; } };
        const describe = (key, item) => fill(root.dataset[key], { type: item.dataset.label, position: items().indexOf(item) + 1, count: items().length });

        const refresh = () => {
            const all = items();
            all.forEach((item, index) => {
                item.querySelector("[data-home-up]").disabled = index === 0;
                item.querySelector("[data-home-down]").disabled = index === all.length - 1;
            });
            if (preview) {
                preview.replaceChildren(...all.filter(item => item.querySelector("[data-home-shown]").checked).map(item => {
                    const entry = document.createElement("li");
                    entry.textContent = item.dataset.label;
                    return entry;
                }));
            }
        };

        const place = (item, index) => {
            const all = items();
            const target = Math.max(0, Math.min(all.length - 1, index));
            if (all[target] === item) { return false; }
            list.insertBefore(item, target > all.indexOf(item) ? all[target].nextSibling : all[target]);
            return true;
        };

        // Moving a card in the DOM makes the browser blur the focused control; that is not the viewer putting the card down.
        const move = (item, delta, focusSelector) => {
            moving = true;
            if (place(item, items().indexOf(item) + delta)) {
                refresh();
                announce(describe("textMoved", item));
            }
            const focus = item.querySelector(focusSelector);
            if (focus && !focus.disabled) { focus.focus(); }
            moving = false;
        };

        const drop = handle => {
            if (handle.getAttribute("aria-pressed") !== "true") { return; }
            handle.setAttribute("aria-pressed", "false");
            handle.closest("[data-home-item]").classList.remove("is-grabbed");
            announce(describe("textDropped", handle.closest("[data-home-item]")));
        };

        list.addEventListener("click", event => {
            const item = event.target.closest("[data-home-item]");
            if (!item) { return; }
            if (event.target.closest("[data-home-up]")) { move(item, -1, "[data-home-up]"); }
            else if (event.target.closest("[data-home-down]")) { move(item, 1, "[data-home-down]"); }
        });

        list.addEventListener("change", event => { if (event.target.matches("[data-home-shown]")) { refresh(); } });

        list.addEventListener("keydown", event => {
            const handle = event.target.closest("[data-home-handle]");
            if (!handle) { return; }
            const item = handle.closest("[data-home-item]");
            const grabbed = handle.getAttribute("aria-pressed") === "true";
            if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                if (grabbed) { drop(handle); } else {
                    handle.setAttribute("aria-pressed", "true");
                    item.classList.add("is-grabbed");
                    announce(describe("textGrabbed", item));
                }
            } else if (event.key === "Escape" && grabbed) {
                event.preventDefault();
                drop(handle);
            } else if (event.key === "ArrowUp" || event.key === "ArrowDown") {
                event.preventDefault();
                const delta = event.key === "ArrowUp" ? -1 : 1;
                if (grabbed) { move(item, delta, "[data-home-handle]"); } else {
                    const neighbour = items()[items().indexOf(item) + delta];
                    if (neighbour) { neighbour.querySelector("[data-home-handle]").focus(); }
                }
            }
        });

        list.addEventListener("focusout", event => { if (!moving && event.target.matches("[data-home-handle]")) { drop(event.target); } });

        // Dragging works for a mouse and for a finger alike: the handle captures the pointer and the card follows the pointer's height.
        list.addEventListener("pointerdown", event => {
            const handle = event.target.closest("[data-home-handle]");
            if (!handle || (event.pointerType === "mouse" && event.button !== 0)) { return; }
            const item = handle.closest("[data-home-item]");
            handle.setPointerCapture(event.pointerId);
            item.classList.add("is-dragging");
            const onMove = moveEvent => {
                const all = items();
                const over = all.find(other => other !== item && moveEvent.clientY >= other.getBoundingClientRect().top && moveEvent.clientY <= other.getBoundingClientRect().bottom);
                if (over && place(item, all.indexOf(over))) { refresh(); }
            };
            const end = () => {
                handle.removeEventListener("pointermove", onMove);
                handle.removeEventListener("pointerup", end);
                handle.removeEventListener("pointercancel", end);
                item.classList.remove("is-dragging");
                announce(describe("textDropped", item));
            };
            handle.addEventListener("pointermove", onMove);
            handle.addEventListener("pointerup", end);
            handle.addEventListener("pointercancel", end);
        });

        // A preset only fills the editor: the types it names come first and are shown, every other card follows, hidden.
        root.querySelectorAll("[data-home-preset]").forEach(button => button.addEventListener("click", () => {
            const wanted = button.dataset.homePreset.split(",");
            const all = items();
            const first = wanted.map(id => all.find(item => item.dataset.id === id)).filter(Boolean);
            const rest = all.filter(item => !first.includes(item));
            [...first, ...rest].forEach(item => list.appendChild(item));
            all.forEach(item => { item.querySelector("[data-home-shown]").checked = first.includes(item); });
            refresh();
            announce(button.textContent);
        }));

        root.addEventListener("home-layout:refresh", refresh);
        refresh();
        root.classList.add("is-ready");
    };

    document.querySelectorAll("[data-home-layout]").forEach(init);
})();
