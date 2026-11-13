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

    // What the person just activated. A drawer with an address (a routed one) opens by navigating, and the router moves focus to the page's
    // heading before the stack looks at it, so the control that opened the drawer is read from the click, not from where focus is by then.
    let activated = null;
    let activatedAt = 0;
    document.addEventListener("click", (event) => {
        const control = event.target instanceof Element ? event.target.closest("button, a[href], [role=button], [role=menuitem]") : null;
        if (control) {
            activated = control;
            activatedAt = Date.now();
        }
    }, { capture: true });
    const openerNow = () =>
        activated && activated.isConnected && !activated.closest(".pdk-sheet") && Date.now() - activatedAt < 2000 ? activated : document.activeElement;

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
                    opener.push(openerNow());
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
                // A routed drawer closes by navigating back, and the router then focuses the heading: take focus back once it has.
                setTimeout(() => {
                    if (back.isConnected && !topSheet() && (document.activeElement === document.body || document.activeElement?.tagName === "H1")) {
                        back.focus({ preventScroll: true });
                    }
                }, 150);
            } else {
                const top = topSheet();
                if (top) {
                    top.focus({ preventScroll: true });
                }
            }
        },
    });

    // Drawers leaving: Blazor removes a closed level's sheet (and the stack, with its backdrop, once the last level goes) at once. The
    // node is put back outside Blazor's tree, inert, before the next paint; it slides out from wherever it was (a level under the top
    // from its pushed place) and is dropped. A detached element forgets its scroll position, so the sheets' are kept as they scroll.
    const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
    const scrolled = new WeakMap();
    document.addEventListener("scroll", (event) => {
        const target = event.target;
        if (target instanceof Element && target.closest(".pdk-sheet")) {
            scrolled.set(target, target.scrollTop);
        }
    }, { capture: true, passive: true });

    const leave = (node) => {
        node.classList.add("pdk-leaving");
        // No longer a level: what looks for the top drawer (Escape, focus, a test) must not find the one on its way out.
        for (const element of [node, ...node.querySelectorAll("[data-top]")]) {
            element.removeAttribute("data-top");
        }

        node.inert = true;
        node.setAttribute("aria-hidden", "true");
        document.body.appendChild(node);
        for (const element of node.querySelectorAll("*")) {
            const top = scrolled.get(element);
            if (top) {
                element.scrollTop = top;
            }
        }

        const sheets = node.matches(".pdk-sheet") ? [node] : [...node.querySelectorAll(".pdk-sheet")];
        const animations = sheets.map((sheet) => sheet.animate(
            [{ transform: getComputedStyle(sheet).transform }, { transform: "translateX(100%)" }],
            { duration: 280, easing: "cubic-bezier(0.32, 0.72, 0, 1)", fill: "forwards" }));
        for (const backdrop of node.querySelectorAll(".pdk-stack-backdrop")) {
            animations.push(backdrop.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 220, easing: "ease-out", fill: "forwards" }));
        }

        Promise.allSettled(animations.map((animation) => animation.finished)).then(() => node.remove());
    };

    new MutationObserver((records) => {
        if (reducedMotion.matches || !document.body) {
            return;
        }

        for (const record of records) {
            for (const node of record.removedNodes) {
                if (node.nodeType === Node.ELEMENT_NODE && !node.isConnected && !node.classList.contains("pdk-leaving")
                    && (node.classList.contains("pdk-sheet") || node.classList.contains("pdk-stack"))) {
                    leave(node);
                }
            }
        }
    }).observe(document.documentElement, { childList: true, subtree: true });

    // The connection lost (App.razor's #components-reconnect-modal): Blazor shows it and says how it goes; the buttons are these. A failed
    // reconnect tries again when the tab is looked at again, as a phone that slept comes back.
    const reconnectModal = document.getElementById("components-reconnect-modal");
    if (reconnectModal) {
        const whenVisible = () => {
            if (document.visibilityState === "visible") {
                retry();
            }
        };
        const retry = async () => {
            document.removeEventListener("visibilitychange", whenVisible);
            try {
                if (await window.Blazor.reconnect()) {
                    return;
                }

                // The server no longer knows the circuit: carry its state over to a new one if it can, else start afresh.
                if (!(await window.Blazor.resumeCircuit())) {
                    window.location.reload();
                }
            } catch {
                document.addEventListener("visibilitychange", whenVisible);
            }
        };
        const resume = async () => {
            try {
                if (!(await window.Blazor.resumeCircuit())) {
                    window.location.reload();
                }
            } catch {
                reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
            }
        };

        reconnectModal.addEventListener("components-reconnect-state-changed", (event) => {
            if (event.detail.state === "failed") {
                document.addEventListener("visibilitychange", whenVisible);
            }
        });
        reconnectModal.addEventListener("click", (event) => {
            const action = event.target instanceof Element ? event.target.closest("[data-reconnect]")?.dataset.reconnect : null;
            if (action === "retry") {
                retry();
            } else if (action === "resume") {
                resume();
            } else if (action === "reload") {
                window.location.reload();
            }
        });
    }

    // The log drawer's pane: where its end is and whether the person is still there, so following can pause when they scroll up and
    // resume when they come back. The component is only called back while it watches (one pane at a time: one drawer shows a log).
    let logListener = null;
    const logPane = Object.freeze({
        /** Whether the pane is at its end (within a couple of lines, so a partly visible last line still counts). */
        atEnd(element) {
            return !!element && element.scrollHeight - element.scrollTop - element.clientHeight <= 24;
        },

        /** Scrolls the pane to its end (turning Follow on, or jumping back to the latest lines). */
        toEnd(element) {
            if (element) {
                element.scrollTop = element.scrollHeight;
            }
        },

        /** Reports the pane's end on every scroll; the component pauses or resumes following from it. */
        watch(element, dotNet) {
            logPane.detach();
            if (!element) {
                return;
            }

            const report = () => {
                try {
                    dotNet.invokeMethodAsync("LogScrolledAsync", logPane.atEnd(element)).catch(() => {});
                } catch {
                    // The circuit is already gone.
                }
            };

            report();
            element.addEventListener("scroll", report, { passive: true });
            logListener = () => element.removeEventListener("scroll", report);
        },

        detach() {
            if (logListener) {
                logListener();
                logListener = null;
            }
        },
    });

    // The security page samples every couple of seconds only while its tab is shown: it is told when the tab is hidden or shown again.
    let visibilityListener = null;
    const pageVisibility = Object.freeze({
        watch(dotNet) {
            pageVisibility.detach();
            const report = () => {
                try {
                    dotNet.invokeMethodAsync("PageVisibilityChangedAsync", document.visibilityState === "visible").catch(() => {});
                } catch {
                    // The circuit is already gone.
                }
            };

            if (document.visibilityState !== "visible") {
                report();
            }

            document.addEventListener("visibilitychange", report);
            visibilityListener = () => document.removeEventListener("visibilitychange", report);
        },

        detach() {
            if (visibilityListener) {
                visibilityListener();
                visibilityListener = null;
            }
        },
    });

    // A multi-line text that starts like a URL scheme ("User-agent: *" reads as the scheme "user-agent:") is put on the iOS
    // clipboard as a URL too, and Safari pastes that form: lowercased and percent-encoded, every newline a %0A. A field that
    // opts in (data-paste="decode-url") gets the text back. Only a paste with an encoded line break and no real one is
    // touched, so ordinary text, and a single URL someone means to paste, go in as they are.
    document.addEventListener("paste", (event) => {
        const field = event.target;
        if (!(field instanceof HTMLTextAreaElement) || !field.closest("[data-paste='decode-url']") || !event.clipboardData) {
            return;
        }

        const pasted = event.clipboardData.getData("text/plain");
        if (!/%0[ad]/i.test(pasted) || /[\r\n]/.test(pasted)) {
            return;
        }

        let decoded;
        try {
            decoded = decodeURIComponent(pasted);
        } catch {
            return;
        }

        event.preventDefault();
        // insertText keeps the undo history and raises the input event the field's binding listens to.
        if (!document.execCommand("insertText", false, decoded)) {
            field.setRangeText(decoded, field.selectionStart, field.selectionEnd, "end");
            field.dispatchEvent(new Event("input", { bubbles: true }));
        }
    }, { capture: true });

    // A CopyableCode on a plain-HTML page (sign-in, setup) has no component behind it: its button carries the text.
    document.addEventListener("click", async (event) => {
        const button = event.target instanceof Element ? event.target.closest("[data-copy-value]") : null;
        if (!button) {
            return;
        }

        const label = button.getAttribute("aria-label");
        try {
            await navigator.clipboard.writeText(button.dataset.copyValue);
            button.title = "Copied";
            button.setAttribute("aria-label", "Copied");
        } catch {
            button.title = "Select the text and copy it yourself";
        }

        setTimeout(() => {
            button.title = "Copy";
            button.setAttribute("aria-label", label);
        }, 2000);
    });

    // A SegmentedControl's or ChoiceCards' arrow keys move its choice; the page must not also scroll.
    document.addEventListener("keydown", (event) => {
        if (event.key.startsWith("Arrow") && event.target instanceof Element && event.target.closest(".pdk-seg, .pdk-choice-cards")) {
            event.preventDefault();
        }
    });

    window.paduraksa = Object.freeze({
        drawers,
        logPane,
        pageVisibility,

        /** Stores the person's choice ("dark" or "light") and applies it to the plain-HTML pages' colours. */
        setTheme(value) {
            if (value !== "dark" && value !== "light") {
                return;
            }

            write("paduraksa-theme", value);
            document.documentElement.dataset.theme = value;
        },

        /** Moves focus to the `index`th segment of a SegmentedControl (the arrow keys changed the choice). */
        focusSegment(group, index) {
            group?.querySelectorAll("[role=radio]")[index]?.focus();
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
