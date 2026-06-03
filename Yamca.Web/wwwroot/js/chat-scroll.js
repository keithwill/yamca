window.yamcaChat = (function () {
    const STICK_THRESHOLD_PX = 80;
    const MATCH_TOLERANCE_PX = 2;

    // Per-scroller state, so multiple chat panels (split view) don't fight over
    // a single shared scroll position.
    const states = new WeakMap();

    function getState(scroller) {
        let s = states.get(scroller);
        if (!s) {
            s = { stick: true, expectedScrollTop: -1, pending: false, attached: false };
            states.set(scroller, s);
        }
        return s;
    }

    function distanceFromBottom(scroller) {
        return scroller.scrollHeight - (scroller.scrollTop + scroller.clientHeight);
    }

    function onUserScroll(scroller, s) {
        // Value-based suppression: if the new scroll position matches the one we
        // just programmatically scrolled to, it's our own event — consume it and
        // leave `stick` alone. Any other position is user intent.
        if (s.expectedScrollTop !== -1 &&
            Math.abs(scroller.scrollTop - s.expectedScrollTop) <= MATCH_TOLERANCE_PX) {
            s.expectedScrollTop = -1;
            return;
        }
        s.stick = distanceFromBottom(scroller) <= STICK_THRESHOLD_PX;
    }

    function attach(scroller) {
        const s = getState(scroller);
        if (s.attached) return;
        s.attached = true;
        scroller.addEventListener("scroll", () => onUserScroll(scroller, s), { passive: true });
    }

    function bytesToBase64(bytes) {
        // Chunked conversion avoids exceeding the argument-count limit of
        // String.fromCharCode on large screenshots.
        let binary = "";
        const CHUNK = 0x8000;
        for (let i = 0; i < bytes.length; i += CHUNK) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + CHUNK));
        }
        return btoa(binary);
    }

    async function handlePaste(event, dotNetRef) {
        const items = event.clipboardData && event.clipboardData.items;
        if (!items) return;
        const blobs = [];
        for (const item of items) {
            if (item.kind === "file" && item.type && item.type.indexOf("image/") === 0) {
                const file = item.getAsFile();
                if (file) blobs.push(file);
            }
        }
        if (blobs.length === 0) return; // let normal text paste proceed
        event.preventDefault();
        for (const blob of blobs) {
            try {
                const buffer = await blob.arrayBuffer();
                const base64 = bytesToBase64(new Uint8Array(buffer));
                await dotNetRef.invokeMethodAsync("OnImagePasted", blob.type || "image/png", base64);
            } catch (e) {
                // Blob unreadable or circuit gone — skip this image.
            }
        }
    }

    function findScrollerFor(anchor) {
        // The anchor lives at the bottom of the .yamca-scroll container; walk up.
        let el = anchor;
        while (el && !(el.classList && el.classList.contains("yamca-scroll"))) {
            el = el.parentElement;
        }
        return el;
    }

    function scrollNow(scroller, anchor) {
        try {
            if (anchor && typeof anchor.scrollIntoView === "function") {
                anchor.scrollIntoView({ block: "end", behavior: "auto" });
            } else if (scroller) {
                scroller.scrollTop = scroller.scrollHeight;
            }
            if (scroller) {
                const s = getState(scroller);
                s.expectedScrollTop = scroller.scrollTop;
            }
        } catch (e) {
            // Element ref torn down or DOM unavailable — ignore.
        }
    }

    return {
        init: function (scrollContainer) {
            if (scrollContainer) attach(scrollContainer);
        },
        scrollIfSticky: function (anchor) {
            if (!anchor) return;
            const scroller = findScrollerFor(anchor);
            if (!scroller) return;
            const s = getState(scroller);
            if (!s.stick || s.pending) return;
            s.pending = true;
            requestAnimationFrame(function () {
                s.pending = false;
                if (s.stick) scrollNow(scroller, anchor);
            });
        },
        reset: function (scrollContainer) {
            if (!scrollContainer) return;
            const s = getState(scrollContainer);
            s.stick = true;
        },
        initPaste: function (composer, dotNetRef) {
            if (!composer || !dotNetRef) return;
            const s = getState(composer);
            if (s.pasteAttached) return;
            s.pasteAttached = true;
            s.pasteHandler = function (event) { handlePaste(event, dotNetRef); };
            composer.addEventListener("paste", s.pasteHandler);
        },
        disposePaste: function (composer) {
            if (!composer) return;
            const s = getState(composer);
            if (s.pasteHandler) composer.removeEventListener("paste", s.pasteHandler);
            s.pasteAttached = false;
            s.pasteHandler = null;
        },
        copyText: function (text) {
            if (text == null) return;
            if (navigator.clipboard && navigator.clipboard.writeText) {
                return navigator.clipboard.writeText(text);
            }
            // Fallback for non-secure contexts where the async clipboard API is unavailable.
            const ta = document.createElement("textarea");
            ta.value = text;
            ta.style.position = "fixed";
            ta.style.opacity = "0";
            document.body.appendChild(ta);
            ta.select();
            try { document.execCommand("copy"); } finally { document.body.removeChild(ta); }
        }
    };
})();
