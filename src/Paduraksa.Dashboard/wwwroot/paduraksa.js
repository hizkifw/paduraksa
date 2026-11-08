// Paduraksa dashboard: the little the page needs besides Blazor and MudBlazor (the theme cookies, the drawer stack's Escape and focus, the
// clipboard, saving a file). Served from this origin (CSP script-src 'self').
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

    // The drawer stack (DrawerHost): Escape for the top drawer, and focus that follows the stack.
    const opener = [];
    let asked = null;
    let escapeListener = null;
    const topSheet = () => document.querySelector(".pdk-sheet[data-top]");
    const focusInto = (sheet) => {
        if (!sheet || sheet.contains(document.activeElement)) {
            return;
        }

        const target = sheet.querySelector("[autofocus], .pdk-drawer-title") ?? sheet;
        target.focus({ preventScroll: true });
    };

    const drawers = Object.freeze({
        /** Hands Escape to the host while focus is in the top drawer, unless an open list or menu, or a dialog, takes it first. */
        attach(host) {
            if (escapeListener) {
                document.removeEventListener("keydown", escapeListener);
            }

            escapeListener = (event) => {
                if (event.key !== "Escape" || event.defaultPrevented || event.isComposing) {
                    return;
                }

                const top = topSheet();
                if (!top || document.querySelector(".mud-popover-open:not(.mud-tooltip)") || document.querySelector(".mud-dialog-container")) {
                    return;
                }

                if (event.target !== document.body && !top.contains(event.target)) {
                    return;
                }

                event.preventDefault();
                host.invokeMethodAsync("EscapeAsync");
            };
            document.addEventListener("keydown", escapeListener);
        },

        detach() {
            if (escapeListener) {
                document.removeEventListener("keydown", escapeListener);
                escapeListener = null;
            }

            opener.length = 0;
        },

        /** A level took the place of the top one (one host's drawer for another's): focus into it. */
        focusTop() {
            setTimeout(() => focusInto(topSheet()), 0);
        },

        /** "Discard?" shows over the top drawer: it takes focus (the drawer under it is inert), and remembers where focus was. */
        ask() {
            asked = document.activeElement;
            const keep = document.querySelector(".pdk-sheet[data-top] [data-drawer=keep]");
            if (keep) {
                keep.focus({ preventScroll: true });
            }
        },

        /** The question went: focus goes back to where it was, if that is still there. */
        answered() {
            const back = asked;
            asked = null;
            if (back && back.isConnected && !back.closest("[inert]")) {
                back.focus({ preventScroll: true });
            }
        },

        /** The stack is now `depth` levels deep: focus a level that opened, or go back to what opened the levels that closed. */
        sync(depth) {
            if (depth > opener.length) {
                while (opener.length < depth) {
                    opener.push(document.activeElement);
                }

                // After the drawer's own fields had their turn (a new form focuses its first field).
                setTimeout(() => focusInto(topSheet()), 0);
                return;
            }

            let back = null;
            while (opener.length > depth) {
                back = opener.pop();
            }

            if (back && back.isConnected && !back.closest("[inert]")) {
                back.focus({ preventScroll: true });
            } else {
                const top = topSheet();
                if (top) {
                    top.focus({ preventScroll: true });
                }
            }
        },
    });

    window.paduraksa = Object.freeze({
        drawers,

        /** Stores the person's choice ("dark" or "light") and applies it to the plain-HTML pages' colours. */
        setTheme(value) {
            if (value !== "dark" && value !== "light") {
                return;
            }

            write("paduraksa-theme", value);
            document.documentElement.dataset.theme = value;
        },

        /** Copies text to the clipboard; false when the browser refused (no secure context, no permission). */
        async copyText(text) {
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch {
                return false;
            }
        },

        /** Saves a file the page generated (recovery codes, an audit export) from a .NET stream, without a request to the server. */
        async download(fileName, contentType, stream) {
            const buffer = await stream.arrayBuffer();
            const url = URL.createObjectURL(new Blob([buffer], { type: contentType }));
            const link = document.createElement("a");
            link.href = url;
            link.download = fileName;
            link.rel = "noopener";
            document.body.appendChild(link);
            link.click();
            link.remove();
            setTimeout(() => URL.revokeObjectURL(url), 10000);
        },
    });
})();
