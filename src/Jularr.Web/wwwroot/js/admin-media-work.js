(() => {
    "use strict";

    // A season checkbox is a shortcut for the episode checkboxes of its season: it is never submitted itself, so it
    // stays hidden without this script. Its checked / indeterminate state follows the episodes, so the form always
    // posts the episodes it shows.
    const sync = group => {
        const episodes = [...group.querySelectorAll("input[data-episode-monitor]")];
        const season = group.querySelector("input[data-season-monitor]");
        if (!season) {
            return;
        }

        season.closest("label").hidden = false;
        const checked = episodes.filter(episode => episode.checked).length;
        season.checked = episodes.length > 0 && checked === episodes.length;
        season.indeterminate = checked > 0 && checked < episodes.length;
    };

    document.querySelectorAll("[data-season-group]").forEach(group => {
        sync(group);
        group.addEventListener("change", event => {
            const target = event.target;
            if (target.matches("input[data-season-monitor]")) {
                group.querySelectorAll("input[data-episode-monitor]").forEach(episode => {
                    episode.checked = target.checked;
                });
            }

            sync(group);
        });
    });
})();
