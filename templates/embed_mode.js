(function () {
    "use strict";

    var VIEW_PATHS = {
        settings: "/",
        processing: "/processing",
        analysis: "/analysis",
    };

    var params = new URLSearchParams(window.location.search);
    var enabled = params.get("embed") === "1";
    var requested = params.get("view") || "";
    var initialPath = enabled ? (VIEW_PATHS[requested] || "/") : "/";

    window.__invoiceFlowEmbed = { enabled: enabled, initialPath: initialPath };

    if (enabled && document.body) {
        document.body.classList.add("is-embedded");
    }
})();
