// Centralized application JS entry point.

(function () {
    const isDashboard = document.querySelector(".panel-grid");
    if (!isDashboard) return;

    const REFRESH_MS = 15000;
    setInterval(() => {
        window.location.reload();
    }, REFRESH_MS);
})();

// Real-time telemetry via SignalR (server-pushed, no fake animation).
// Requires the SignalR client script to be loaded on the page; see
// _Layout.cshtml. If unavailable (script not loaded, hub unreachable),
// this fails silently rather than fabricating live data.
(function () {
    if (typeof signalR === "undefined") return;

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/agent")
        .withAutomaticReconnect()
        .build();

    connection.on("HardwareTelemetryUpdated", (snapshot) => {
        const el = document.querySelector("[data-telemetry-cpu]");
        if (el) el.textContent = snapshot.cpuPercent != null ? `${snapshot.cpuPercent}%` : "Unavailable";
    });

    connection.on("AgentStateChanged", (evt) => {
        console.debug("AgentStateChanged", evt);
    });

    connection.on("VerificationUpdated", (evt) => {
        console.debug("VerificationUpdated", evt);
    });

    connection.start().catch((err) => console.warn("SignalR connection failed:", err));
})();
