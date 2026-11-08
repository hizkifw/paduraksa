// Paduraksa dashboard: the little the page needs besides Blazor and MudBlazor. Served from this origin (CSP script-src 'self').
// It remembers the palette choice and the system's light/dark setting in two cookies the server reads on the next load, so the
// first paint already has the right colours. Neither cookie is a secret.
(() => {
    "use strict";

    const secure = window.location.protocol === "https:" ? "; Secure" : "";
    const write = (name, value) => {
        document.cookie = `${name}=${value}; Path=/; Max-Age=31536000; SameSite=Strict${secure}`;
    };

    const light = window.matchMedia("(prefers-color-scheme: light)");
    const recordScheme = () => write("paduraksa-scheme", light.matches ? "light" : "dark");
    recordScheme();
    light.addEventListener("change", recordScheme);

    window.paduraksa = Object.freeze({
        /** Stores the person's choice ("dark" or "light") and applies it to the plain-HTML pages' colours. */
        setTheme(value) {
            if (value !== "dark" && value !== "light") {
                return;
            }

            write("paduraksa-theme", value);
            document.documentElement.dataset.theme = value;
        },
    });
})();
