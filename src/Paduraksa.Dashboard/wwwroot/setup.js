// The setup page's password strength hint. The page is a plain form and works without this; with it, the Owner sees how strong the
// password looks while typing. Served from this origin (CSP script-src 'self'); the password never leaves the field.
(() => {
    "use strict";

    const field = document.querySelector("input[data-strength]");
    const meter = document.querySelector("[data-strength-meter]");
    const label = document.querySelector("[data-strength-label]");
    if (!field || !meter || !label) {
        return;
    }

    const min = Number(field.getAttribute("minlength")) || 12;
    const common = /^(password|passw0rd|123456|qwerty|letmein|admin|welcome|iloveyou|monkey|dragon)/i;
    const levels = ["Too short", "Weak", "Fair", "Good", "Strong"];

    const score = (value) => {
        if (value.length < min) {
            return 0;
        }

        let classes = 0;
        for (const pattern of [/[a-z]/, /[A-Z]/, /[0-9]/, /[^A-Za-z0-9]/]) {
            classes += pattern.test(value) ? 1 : 0;
        }

        let points = 1;
        points += value.length >= min + 4 ? 1 : 0;
        points += value.length >= 20 ? 1 : 0;
        points += classes >= 3 || value.includes(" ") ? 1 : 0;
        if (common.test(value) || /^(.)\1+$/.test(value) || new Set(value).size < 5) {
            points = 1;
        }

        return Math.min(points, 4);
    };

    const update = () => {
        const value = field.value;
        const level = value.length === 0 ? -1 : score(value);
        meter.dataset.level = String(level);
        label.textContent = level < 0 ? "" : levels[level];
    };

    field.addEventListener("input", update);
    update();
})();
