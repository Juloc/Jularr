// Admin Acquisition Profiles: reorders and adds the ranked qualities and adds, reorders or removes rules. Every control posts through the
// ordinary form fields, so what the server saves is exactly what is on the page; without this script the stored rows stay editable in place.
(() => {
    const form = document.querySelector(".admprofiles-form");
    if (!form) {
        return;
    }

    const list = form.querySelector("[data-quality-list]");
    const qualityTemplate = form.querySelector("[data-quality-template]");
    const unranked = form.querySelector("[data-quality-unranked]");
    const noFinal = form.querySelector('.admprofiles-quality-none input[type="radio"]');

    const syncUnranked = () => {
        unranked.hidden = unranked.querySelectorAll("[data-unranked]").length === 0;
    };

    const replaceToken = (root, token, value) => {
        for (const element of [root, ...root.querySelectorAll("*")]) {
            for (const attribute of [...element.attributes]) {
                if (attribute.value.includes(token)) {
                    attribute.value = attribute.value.replaceAll(token, value);
                }
            }

            if (element.children.length === 0 && element.textContent.includes(token)) {
                element.textContent = element.textContent.replaceAll(token, value);
            }
        }
    };

    form.addEventListener("click", (event) => {
        const target = event.target instanceof Element ? event.target.closest("button") : null;
        if (!target) {
            return;
        }

        if (target.dataset.move) {
            const row = target.closest("li, [data-rule-row]");
            const neighbour = target.dataset.move === "up" ? row.previousElementSibling : row.nextElementSibling;
            if (neighbour) {
                target.dataset.move === "up" ? neighbour.before(row) : neighbour.after(row);
                target.focus();
            }
        } else if ("rankRemove" in target.dataset) {
            const row = target.closest("li");
            const quality = row.dataset.quality;
            if (row.querySelector('input[type="radio"]')?.checked && noFinal) {
                noFinal.checked = true;
            }

            const chip = document.createElement("li");
            chip.dataset.unranked = quality;
            chip.innerHTML = '<button type="button" class="admprofiles-chip"></button>';
            const button = chip.firstElementChild;
            button.dataset.rankAdd = quality;
            button.textContent = quality;
            unranked.querySelector("ul").append(chip);
            row.remove();
            syncUnranked();
            button.focus();
        } else if (target.dataset.rankAdd) {
            const quality = target.dataset.rankAdd;
            const row = qualityTemplate.content.querySelector("li").cloneNode(true);
            replaceToken(row, "__quality__", quality);
            row.dataset.quality = quality;
            list.append(row);
            target.closest("li").remove();
            syncUnranked();
            row.querySelector("[data-move]").focus();
        } else if ("removeRow" in target.dataset) {
            target.closest("[data-rule-row]")?.remove();
        } else if ("repeatAdd" in target.dataset) {
            const wrapper = target.closest("[data-repeat]");
            const index = Number(wrapper.dataset.next);
            const row = wrapper.querySelector("template[data-repeat-template]").content.querySelector("[data-rule-row]").cloneNode(true);
            replaceToken(row, "__index__", String(index));
            wrapper.dataset.next = String(index + 1);
            wrapper.querySelector("[data-repeat-list]").append(row);
            row.querySelector("input:not([type=hidden]), select")?.focus();
        }
    });
})();
