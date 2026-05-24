window.yamcaChat = (function () {
    const STICK_THRESHOLD_PX = 80;
    const MATCH_TOLERANCE_PX = 2;

    let stick = true;
    let expectedScrollY = -1;
    let pending = false;
    let attached = false;

    function distanceFromBottom() {
        const doc = document.documentElement;
        return doc.scrollHeight - (window.scrollY + window.innerHeight);
    }

    function onUserScroll() {
        // Value-based suppression: if the new scroll position matches the one we
        // just programmatically scrolled to, it's our own event — consume it and
        // leave `stick` alone. Any other position is user intent, regardless of
        // how recently we scrolled. A time-based window doesn't work here because
        // streaming tokens trigger programmatic scrolls faster than any sane
        // window length, so it would perpetually swallow user scrolls.
        if (expectedScrollY !== -1 &&
            Math.abs(window.scrollY - expectedScrollY) <= MATCH_TOLERANCE_PX) {
            expectedScrollY = -1;
            return;
        }
        stick = distanceFromBottom() <= STICK_THRESHOLD_PX;
    }

    function attach() {
        if (attached) return;
        attached = true;
        window.addEventListener("scroll", onUserScroll, { passive: true });
    }

    function scrollNow(element) {
        try {
            if (element && typeof element.scrollIntoView === "function") {
                element.scrollIntoView({ block: "end", behavior: "auto" });
            } else {
                window.scrollTo(0, document.documentElement.scrollHeight);
            }
            expectedScrollY = window.scrollY;
        } catch (e) {
            // Element ref torn down or DOM unavailable — ignore.
        }
    }

    return {
        init: function () {
            attach();
        },
        scrollIfSticky: function (element) {
            attach();
            if (!stick || pending) return;
            pending = true;
            requestAnimationFrame(function () {
                pending = false;
                if (stick) scrollNow(element);
            });
        },
        reset: function () {
            stick = true;
        }
    };
})();
