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

    const IMAGE_EXTS = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    function extOf(name) {
        const i = name.lastIndexOf(".");
        return i >= 0 ? name.substring(i).toLowerCase() : "";
    }

    function isImageFile(file) {
        if (file.type && file.type.indexOf("image/") === 0) return true;
        return IMAGE_EXTS.indexOf(extOf(file.name)) >= 0;
    }

    function parseAccept(acceptCsv) {
        const set = new Set();
        if (acceptCsv) {
            for (const part of acceptCsv.split(",")) {
                const ext = part.trim().toLowerCase();
                if (ext) set.add(ext);
            }
        }
        return set;
    }

    // True only for a file drag — ignores text selections, links, etc. so the
    // composer doesn't light up for non-file drags.
    function dragHasFiles(event) {
        const dt = event.dataTransfer;
        if (!dt || !dt.types) return false;
        for (const t of dt.types) if (t === "Files") return true;
        return false;
    }

    async function handleDrop(event, dotNetRef, acceptSet) {
        const dt = event.dataTransfer;
        const files = dt && dt.files;
        if (!files || files.length === 0) return;
        for (const file of files) {
            const image = isImageFile(file);
            if (!image && acceptSet.size > 0 && !acceptSet.has(extOf(file.name))) {
                await dotNetRef.invokeMethodAsync("OnFileDropRejected", file.name);
                continue;
            }
            try {
                if (image) {
                    const buffer = await file.arrayBuffer();
                    const base64 = bytesToBase64(new Uint8Array(buffer));
                    await dotNetRef.invokeMethodAsync("OnImageDropped", file.name, file.type || "image/png", base64);
                } else {
                    const text = await file.text();
                    await dotNetRef.invokeMethodAsync("OnTextFileDropped", file.name, text);
                }
            } catch (e) {
                // File unreadable or circuit gone — skip this one.
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
            // Attach the scroll listener lazily so anchor-only callers (e.g. the subagent
            // transcript, which has no separate init call) still let the user break the stick
            // by scrolling up.
            attach(scroller);
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
        initDrop: function (composer, dotNetRef, acceptCsv) {
            if (!composer || !dotNetRef) return;
            const s = getState(composer);
            if (s.dropAttached) return;
            s.dropAttached = true;
            const acceptSet = parseAccept(acceptCsv);
            // dragenter/dragleave can fire repeatedly as the pointer crosses child
            // elements; a depth counter keeps the highlight stable until the drag
            // truly leaves the composer.
            let depth = 0;
            const clear = function () { depth = 0; composer.classList.remove("yamca-composer-dragover"); };
            s.dropHandlers = {
                dragenter: function (e) {
                    if (!dragHasFiles(e)) return;
                    e.preventDefault();
                    depth++;
                    composer.classList.add("yamca-composer-dragover");
                },
                dragover: function (e) {
                    if (!dragHasFiles(e)) return;
                    e.preventDefault(); // required for the drop event to fire
                    if (e.dataTransfer) e.dataTransfer.dropEffect = "copy";
                },
                dragleave: function (e) {
                    if (!dragHasFiles(e)) return;
                    depth = Math.max(0, depth - 1);
                    if (depth === 0) composer.classList.remove("yamca-composer-dragover");
                },
                drop: function (e) {
                    if (!dragHasFiles(e)) return;
                    e.preventDefault(); // stop the browser from opening the dropped file
                    clear();
                    handleDrop(e, dotNetRef, acceptSet);
                }
            };
            for (const type in s.dropHandlers) {
                composer.addEventListener(type, s.dropHandlers[type]);
            }
        },
        disposeDrop: function (composer) {
            if (!composer) return;
            const s = getState(composer);
            if (s.dropHandlers) {
                for (const type in s.dropHandlers) {
                    composer.removeEventListener(type, s.dropHandlers[type]);
                }
            }
            s.dropAttached = false;
            s.dropHandlers = null;
        },
        focusSelect: function (el) {
            if (!el) return;
            el.focus();
            if (typeof el.select === "function") el.select();
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
