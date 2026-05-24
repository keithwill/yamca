window.yamcaChat = (function () {
    const STICK_THRESHOLD_PX = 80;
    const MATCH_TOLERANCE_PX = 2;

    let stick = true;
    let expectedScrollTop = -1;
    let pending = false;
    let attached = false;
    let scroller = null;

    function distanceFromBottom() {
        if (!scroller) return 0;
        return scroller.scrollHeight - (scroller.scrollTop + scroller.clientHeight);
    }

    function onUserScroll() {
        if (!scroller) return;
        // Value-based suppression: if the new scroll position matches the one we
        // just programmatically scrolled to, it's our own event — consume it and
        // leave `stick` alone. Any other position is user intent, regardless of
        // how recently we scrolled. A time-based window doesn't work here because
        // streaming tokens trigger programmatic scrolls faster than any sane
        // window length, so it would perpetually swallow user scrolls.
        if (expectedScrollTop !== -1 &&
            Math.abs(scroller.scrollTop - expectedScrollTop) <= MATCH_TOLERANCE_PX) {
            expectedScrollTop = -1;
            return;
        }
        stick = distanceFromBottom() <= STICK_THRESHOLD_PX;
    }

    function attach(element) {
        if (element) scroller = element;
        if (attached || !scroller) return;
        attached = true;
        scroller.addEventListener("scroll", onUserScroll, { passive: true });
    }

    function scrollNow(element) {
        try {
            if (element && typeof element.scrollIntoView === "function") {
                element.scrollIntoView({ block: "end", behavior: "auto" });
            } else if (scroller) {
                scroller.scrollTop = scroller.scrollHeight;
            }
            if (scroller) expectedScrollTop = scroller.scrollTop;
        } catch (e) {
            // Element ref torn down or DOM unavailable — ignore.
        }
    }

    return {
        init: function (scrollContainer) {
            attach(scrollContainer);
        },
        scrollIfSticky: function (element) {
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
