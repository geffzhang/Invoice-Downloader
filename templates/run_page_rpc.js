(function (root, factory) {
    "use strict";
    const api = factory();
    if (typeof module === "object" && typeof module.exports === "object") {
        module.exports = api;
    } else {
        root.RunPageRpc = api;
    }
})(typeof window !== "undefined" ? window : globalThis, function () {
    "use strict";

    function loadRunContext(rpcClient) {
        return rpcClient.call("run.context.get", null).then((context) => ({
            explicit_run_context: !!context.explicitRunContext,
            controlled_run: !!context.controlledRun,
            autostart_enabled: !!context.autostartEnabled,
            autostart_delay_ms: context.autostartDelayMs || 0,
            autostart_token: context.autostartToken || "",
            run_id: context.runId || "",
            locked_output_path: context.lockedOutputPath || "",
            locked_date_from: context.lockedDateFrom || "",
            locked_date_to: context.lockedDateTo || "",
            locked_email: context.lockedEmail || "",
            account_id: context.accountId || "",
        }));
    }

    function startRun(rpcClient, request) {
        return rpcClient.call("run.start", {
            runId: request.runId,
            accountId: request.accountId,
            dateFrom: request.dateFrom,
            dateTo: request.dateTo,
            outputDirectory: request.outputDirectory,
            companyName: request.companyName,
            runMode: request.runMode,
        });
    }

    function mapProgress(snapshot) {
        const value = snapshot || {};
        const stats = value.stats || {};
        return {
            progress: Number(value.progress) || 0,
            status_text: value.statusText || "等待任务开始",
            logs: Array.isArray(value.logs) ? value.logs : [],
            stats: {
                emails: Number(stats.emails) || 0,
                invoices: Number(stats.invoices) || 0,
                errors: Number(stats.errors) || 0,
            },
            is_running: !!value.isRunning,
            run_state: value.runState || "idle",
            last_error: value.lastError || "",
            stop_requested: !!value.stopRequested,
            can_stop: !!value.canStop,
            quota_exhausted: !!value.quotaExhausted,
            quota_message: value.quotaMessage || "",
            mailbox_fetch_failures: Array.isArray(value.mailboxFetchFailures) ? value.mailboxFetchFailures : [],
            build_identity: value.buildIdentity || null,
            raw_date_range: value.rawDateRange || null,
            imap_query_range: value.imapQueryRange || null,
        };
    }

    function getProgress(rpcClient, runId) {
        return rpcClient.call("run.progress.get", runId ? { runId } : null).then(mapProgress);
    }

    function stopRun(rpcClient, runId) {
        return rpcClient.call("run.stop", { runId });
    }

    function mapResults(snapshot) {
        const value = snapshot || {};
        return {
            categories: value.categories || {},
            successInvoices: value.successInvoices || [],
            groupedErrorInvoices: value.groupedErrorInvoices || [],
            manual_check_path: value.manualCheckPath || "",
            output_path: value.outputPath || "",
            summary: value.summary || {},
            resultBreakdown: value.resultBreakdown || {},
            reasonCodeBreakdown: value.reasonCodeBreakdown || {},
            quota_exhausted: !!value.quotaExhausted,
            quota_message: value.quotaMessage || "",
            last_export_path: value.lastExportPath || "",
        };
    }

    function getResults(rpcClient, runId) {
        return rpcClient.call("run.results.get", runId ? { runId } : null).then(mapResults);
    }

    function exportReport(rpcClient, runId) {
        return rpcClient.call("run.report.export", { runId });
    }

    function openRunFolder(rpcClient, runId) {
        return rpcClient.call("run.folder.open", runId ? { runId } : null);
    }

    function openManualReviewFolder(rpcClient, runId) {
        return rpcClient.call("run.manual-review.open", runId ? { runId } : null);
    }

    function openReport(rpcClient, runId, reportPath, contentHash) {
        return rpcClient.call("run.file.open", { runId, reportPath, contentHash });
    }

    function windowCommand(rpcClient, command) {
        if (!["minimize", "maximize", "close"].includes(command)) {
            return Promise.reject(new Error("The window action is unavailable."));
        }
        return rpcClient.call(`window.${command}`, null);
    }

    function applyEvent(current, envelope) {
        const payload = envelope.payload || {};
        if (envelope.event === "run.progress") {
            const progress = Number(payload.percent);
            return {
                ...current,
                progress: Number.isFinite(progress) ? Math.max(0, Math.min(100, progress)) : current.progress,
                status_text: payload.stage || current.status_text,
                is_running: true,
                run_state: "running",
                mailbox_fetch_failures: Array.isArray(payload.mailboxFetchFailures)
                    ? payload.mailboxFetchFailures
                    : current.mailbox_fetch_failures || [],
            };
        }
        const runState = payload.runState || current.run_state;
        return {
            ...current,
            run_state: runState,
            is_running: false,
            can_stop: false,
        };
    }

    function watchProgress(rpcClient, runId, onUpdate) {
        let active = true;
        let snapshotLoaded = false;
        let lastSequence = 0;
        let current = mapProgress(null);
        const bufferedEvents = [];

        function receive(eventName, envelope) {
            if (!active || !envelope || envelope.runId !== runId) return;
            const sequence = Number(envelope.eventSequence);
            if (!Number.isSafeInteger(sequence) || sequence <= lastSequence) return;
            const entry = { ...envelope, event: eventName, eventSequence: sequence };
            if (!snapshotLoaded) {
                bufferedEvents.push(entry);
                return;
            }
            lastSequence = sequence;
            current = applyEvent(current, entry);
            onUpdate(current);
        }

        const progressListener = (envelope) => receive("run.progress", envelope);
        const terminalListener = (envelope) => receive("run.terminal", envelope);
        rpcClient.on("run.progress", progressListener);
        rpcClient.on("run.terminal", terminalListener);

        async function refresh() {
            const sequenceAtRequest = lastSequence;
            const snapshot = await rpcClient.call("run.progress.get", { runId });
            if (!active) return current;
            const needsInitialization = !snapshotLoaded;
            if (needsInitialization || lastSequence === sequenceAtRequest) {
                current = mapProgress(snapshot);
                onUpdate(current);
            }
            if (needsInitialization) {
                snapshotLoaded = true;
                bufferedEvents.sort((left, right) => left.eventSequence - right.eventSequence);
                for (const event of bufferedEvents) receive(event.event, event);
                bufferedEvents.length = 0;
            }
            return current;
        }

        const initial = refresh();
        return {
            initial,
            refresh: () => refresh(false),
            dispose() {
                if (!active) return;
                active = false;
                rpcClient.off("run.progress", progressListener);
                rpcClient.off("run.terminal", terminalListener);
            },
        };
    }

    return {
        loadRunContext,
        startRun,
        getProgress,
        stopRun,
        getResults,
        exportReport,
        openRunFolder,
        openManualReviewFolder,
        openReport,
        windowCommand,
        watchProgress,
    };
});
