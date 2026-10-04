document.querySelectorAll("[data-tree-toggle]").forEach((button) => {
    button.addEventListener("click", () => {
        const open = button.dataset.treeToggle === "open";
        button.closest(".admrec-tree").querySelectorAll("details").forEach((details) => {
            details.open = open;
        });
    });
});
