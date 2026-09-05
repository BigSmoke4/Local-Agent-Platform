// Live hardware telemetry via SignalR (Phase 8, Section 20). Requires
// wwwroot/lib/signalr/dist/browser/signalr.min.js — fetched locally via
// `libman restore` (see README), never a runtime CDN call.
(function () {
    if (typeof signalR === "undefined") {
        console.warn("SignalR client not found at ~/lib/signalr — run 'libman restore' in the Web project. Falling back to static values.");
        return;
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/agent-telemetry")
        .withAutomaticReconnect()
        .build();

    function fmtBytes(bytes) {
        if (bytes === null || bytes === undefined) return "Unavailable";
        return (bytes / 1024 / 1024 / 1024).toFixed(1) + " GB";
    }
    function fmtPct(p) {
        return (p === null || p === undefined) ? "Unavailable" : Math.round(p) + "%";
    }

    connection.on("HardwareTelemetryUpdated", (snapshot) => {
        const cpuEl = document.querySelector("[data-telemetry='cpu']");
        const ramEl = document.querySelector("[data-telemetry='ram']");
        const gpuEl = document.querySelector("[data-telemetry='gpu']");
        const vramEl = document.querySelector("[data-telemetry='vram']");
        if (cpuEl) cpuEl.textContent = fmtPct(snapshot.cpuUtilizationPercent);
        if (ramEl) ramEl.textContent = fmtBytes(snapshot.ramUsedBytes) + " / " + fmtBytes(snapshot.ramTotalBytes);
        if (gpuEl) gpuEl.textContent = fmtPct(snapshot.gpuUtilizationPercent);
        if (vramEl) vramEl.textContent = fmtBytes(snapshot.gpuVramUsedBytes) + " / " + fmtBytes(snapshot.gpuVramTotalBytes);
    });

    connection.start()
        .then(() => connection.invoke("JoinHardwareGroup"))
        .catch(err => console.warn("SignalR connection failed; Dashboard will show the last page-load snapshot only.", err));
})();
