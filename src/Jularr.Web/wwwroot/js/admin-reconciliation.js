document.querySelectorAll("[data-tree-toggle]").forEach((button) => {
    button.addEventListener("click", () => {
        const open = button.dataset.treeToggle === "open";
        document.querySelectorAll(".admrec-tree details").forEach((details) => {
            details.open = open;
        });
    });
});

// Selects and inputs that carry data-autosubmit save their own form on change; a form attribute points at a form outside the table.
document.querySelectorAll("[data-autosubmit]").forEach((control) => {
    control.addEventListener("change", () => {
        if (control.dataset.autosubmit === "nonempty" && control.value === "") {
            return;
        }

        control.form?.requestSubmit();
    });
});

document.querySelectorAll("[data-select-all]").forEach((toggle) => {
    toggle.addEventListener("change", () => {
        document.querySelectorAll(toggle.dataset.selectAll).forEach((box) => {
            if (!box.disabled) {
                box.checked = toggle.checked;
            }
        });
    });
});

// The season picker only narrows the episode choices; it is never saved and never changes an assignment.
document.querySelectorAll("[data-season-filter]").forEach((picker) => {
    const apply = () => {
        document.querySelectorAll("select.admrec-unit").forEach((select) => {
            Array.from(select.options).forEach((option) => {
                const season = option.dataset.season;
                option.hidden = picker.value !== "" && season !== undefined && season !== picker.value && !option.selected;
            });
        });
    };
    picker.addEventListener("change", apply);
    apply();
});

// Every edit is a full page post; keep the reading position so a long mapping table does not jump back to the top.
const scrollKey = "admrec-scroll";
const currentStep = document.querySelector(".admrec")?.dataset.currentStep;
const [savedStep, savedScroll] = (sessionStorage.getItem(scrollKey) ?? "").split(":");
sessionStorage.removeItem(scrollKey);
if (savedStep === currentStep && savedScroll) {
    window.scrollTo(0, Number(savedScroll));
}

document.querySelectorAll(".admrec form[method=post]").forEach((form) => {
    form.addEventListener("submit", () => sessionStorage.setItem(scrollKey, `${currentStep}:${window.scrollY}`));
});

document.addEventListener("click", (event) => {
    document.querySelectorAll("details.admrec-pop[open]").forEach((pop) => {
        if (!pop.contains(event.target)) {
            pop.open = false;
        }
    });
});
