// Live agent session updates via SignalR (Phase 8). On any real state/task event for
// this session, reloads the page so the person always sees current data without
// polling. A full DOM diff/patch is a further increment — see docs/STATUS.md.
(function () {
    const sessionId = document.body.getAttribute("data-agent-session-id");
    if (!sessionId || typeof signalR === "undefined") return;

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/agent-telemetry")
        .withAutomaticReconnect()
        .build();

    let reloadPending = false;
    function scheduleReload() {
        if (reloadPending) return;
        reloadPending = true;
        setTimeout(() => window.location.reload(), 800); // small debounce for bursts of events
    }

    connection.on("AgentSessionUpdated", scheduleReload);
    connection.on("AgentTaskUpdated", scheduleReload);

    connection.start()
        .then(() => connection.invoke("JoinSessionGroup", sessionId))
        .catch(err => console.warn("SignalR connection failed; this page will not auto-refresh.", err));
})();
