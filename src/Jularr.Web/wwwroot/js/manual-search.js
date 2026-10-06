// Manual Search is server-rendered: a changed filter select submits its own form, and a search that calls the indexers
// shows a status line while the next page is on its way.
const showBusy = () => document.querySelector(".bms-busy")?.removeAttribute("hidden");

document.querySelectorAll("form[data-bms-autosubmit]").forEach((form) => {
  form.addEventListener("change", (event) => {
    if (event.target instanceof HTMLSelectElement) {
      form.requestSubmit();
    }
  });
});

document.querySelectorAll("[data-bms-busy]").forEach((link) => {
  link.addEventListener("click", () => {
    link.setAttribute("aria-busy", "true");
    showBusy();
  });
});

document.querySelectorAll("form.bms-unit-form").forEach((form) => form.addEventListener("submit", showBusy));
