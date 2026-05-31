window.yamcaStorage = {
    downloadText: function (filename, content, mime) {
        try {
            var blob = new Blob([content], { type: mime || "application/octet-stream" });
            var url = URL.createObjectURL(blob);
            var a = document.createElement("a");
            a.href = url;
            a.download = filename;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            setTimeout(function () { URL.revokeObjectURL(url); }, 0);
            return true;
        } catch (e) { return false; }
    }
};
