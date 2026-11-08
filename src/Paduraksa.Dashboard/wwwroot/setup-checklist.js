// The overview's "finish setting up" checklist remembers, in this browser, that it was dismissed. Loaded as a module by the
// component (CSP script-src 'self'); nothing secret is stored.
const key = "paduraksa-setup-checklist";

export function isDismissed() {
    try {
        return window.localStorage.getItem(key) === "dismissed";
    } catch {
        return false;
    }
}

export function dismiss() {
    try {
        window.localStorage.setItem(key, "dismissed");
    } catch {
        // Storage refused (private mode): it shows again next time.
    }
}
