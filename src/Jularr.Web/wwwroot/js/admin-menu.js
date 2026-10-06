// Admin "more actions" menus are details elements: only one stays open, and a click elsewhere or Escape closes it
// (Escape hands focus back to the button that opened it).
const menus = () => document.querySelectorAll("details.admin-menu[open]");

document.addEventListener("toggle", (event) => {
  if (!(event.target instanceof HTMLDetailsElement) || !event.target.matches("details.admin-menu") || !event.target.open) {
    return;
  }

  menus().forEach((menu) => {
    if (menu !== event.target) {
      menu.open = false;
    }
  });
}, true);

document.addEventListener("click", (event) => {
  menus().forEach((menu) => {
    if (!menu.contains(event.target)) {
      menu.open = false;
    }
  });
});

document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") {
    return;
  }

  menus().forEach((menu) => {
    menu.open = false;
    menu.querySelector("summary")?.focus();
  });
});
