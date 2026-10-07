(() => {
    "use strict";

    // A connection test of a typed credential is a form post that redirects back to an empty password field, so a credential that worked was gone
    // before Save could keep it. The test runs in place instead: its result replaces the notice and the typed value stays in the field.
    // Without script the post behaves as before.
    async function testInPlace(form, test, env) {
        const response = await env.fetch(test.formAction, { method: "POST", body: env.formData(form), credentials: "same-origin" });
        const result = env.parse(await response.text()).querySelector("[data-provider-feedback]");
        if (!response.ok || !result) {
            return false;
        }

        const notice = env.importNode(result);
        const current = form.querySelector("[data-provider-feedback]");
        if (current) {
            current.replaceWith(notice);
        } else {
            form.querySelector(".prov-subheading").before(notice);
        }

        return true;
    }

    window.JularrProviderTest = { testInPlace };

    if (typeof document === "undefined") {
        return;
    }

    const env = {
        fetch: (url, options) => fetch(url, options),
        formData: form => new FormData(form),
        parse: html => new DOMParser().parseFromString(html, "text/html"),
        importNode: node => document.importNode(node, true)
    };

    document.querySelectorAll(".prov-form").forEach(form => {
        const test = form.querySelector("button[formaction*='handler=Test']");
        if (!test) return;
        test.addEventListener("click", async event => {
            event.preventDefault();
            test.disabled = true;
            let shown = false;
            try {
                shown = await testInPlace(form, test, env);
            } catch {
                shown = false;
            }

            test.disabled = false;
            // The result could not be read here: the plain post shows it on the reloaded page, as it did before.
            if (!shown) form.requestSubmit(test);
        });
    });
})();
