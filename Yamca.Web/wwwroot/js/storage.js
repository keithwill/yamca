window.yamcaStorage = {
    getItem: function (key) {
        try { return window.localStorage.getItem(key); }
        catch (e) { return null; }
    },
    setItem: function (key, value) {
        try { window.localStorage.setItem(key, value); return true; }
        catch (e) { return false; }
    },
    removeItem: function (key) {
        try { window.localStorage.removeItem(key); return true; }
        catch (e) { return false; }
    }
};
