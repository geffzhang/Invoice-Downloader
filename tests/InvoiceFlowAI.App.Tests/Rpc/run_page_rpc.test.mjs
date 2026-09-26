import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const runRpc = require("../../../templates/run_page_rpc.js");
const pagePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../templates/index_app.js");
const pageSource = fs.readFileSync(pagePath, "utf8");

function createRpcClient(handlers = {}) {
    const calls = [];
    const listeners = new Map();
    return {
        calls,
        on(eventName, handler) {
            if (!listeners.has(eventName)) listeners.set(eventName, new Set());
            listeners.get(eventName).add(handler);
        },
        off(eventName, handler) {
            listeners.get(eventName)?.delete(handler);
        },
        call(method, params) {
            calls.push({ method, params });
            return handlers[method] ? handlers[method](params) : Promise.resolve({});
        },
        emit(eventName, envelope) {
            for (const handler of listeners.get(eventName) || []) handler(envelope);
        },
        listenerCount(eventName) {
            return listeners.get(eventName)?.size || 0;
        },
    };
}

function progressSnapshot(overrides = {}) {
    return {
        runState: "running",
        isRunning: true,
        canStop: true,
        stopRequested: false,
        progress: 10,
        statusText: "initializing",
        stats: { emails: 2, invoices: 1, errors: 0 },
        logs: [],
        lastError: null,
        quotaExhausted: false,
        quotaMessage: null,
        ...overrides,
    };
}

function resultData(overrides = {}) {
    return {
        categories: { travel: 1 },
        successInvoices: [{ date: "2026-09-15", amount: "113.00", merchant: "Seller", path: "archive/invoice.pdf" }],
        groupedErrorInvoices: [],
        manualCheckPath: "C:/Invoices/archive/run-1/review",
        outputPath: "C:/Invoices",
        summary: { success_count: 1 },
        resultBreakdown: { resolved: 1 },
        reasonCodeBreakdown: {},
        quotaExhausted: false,
        quotaMessage: null,
        lastExportPath: null,
        ...overrides,
    };
}

test("loads the typed run context and adapts it once for the page", async () => {
    const rpc = createRpcClient({
        "run.context.get": async () => ({
            explicitRunContext: true,
            controlledRun: true,
            autostartEnabled: false,
            runId: "run-1",
            lockedOutputPath: "C:/Invoices",
            lockedDateFrom: "2026-09-01",
            lockedDateTo: "2026-09-30",
            lockedEmail: "buyer@qq.com",
        }),
    });

    const context = await runRpc.loadRunContext(rpc);

    assert.deepEqual(rpc.calls, [{ method: "run.context.get", params: null }]);
    assert.equal(context.explicit_run_context, true);
    assert.equal(context.locked_output_path, "C:/Invoices");
    assert.equal(context.locked_date_from, "2026-09-01");
});

test("starts a run with typed fields only and never forwards credential drafts", async () => {
    const rpc = createRpcClient({ "run.start": async () => ({ accepted: true, runId: "run-1", rejectionCode: null }) });
    const request = {
        runId: "run-1",
        accountId: "account-1",
        dateFrom: "2026-09-01",
        dateTo: "2026-09-30",
        outputDirectory: "C:/Invoices",
        companyName: "Buyer",
        runMode: "full",
        authCode: "must-not-cross-rpc",
        apiKey: "must-not-cross-rpc",
    };

    const result = await runRpc.startRun(rpc, request);

    assert.deepEqual(rpc.calls, [{
        method: "run.start",
        params: {
            runId: "run-1",
            accountId: "account-1",
            dateFrom: "2026-09-01",
            dateTo: "2026-09-30",
            outputDirectory: "C:/Invoices",
            companyName: "Buyer",
            runMode: "full",
        },
    }]);
    assert.equal(result.accepted, true);
});

test("loads typed progress and sends stop for the explicit run id", async () => {
    const rpc = createRpcClient({
        "run.progress.get": async () => progressSnapshot(),
        "run.stop": async () => ({ accepted: true, alreadyRequested: true, errorCode: null }),
    });

    const progress = await runRpc.getProgress(rpc, "run-1");
    const stopped = await runRpc.stopRun(rpc, "run-1");

    assert.equal(progress.run_state, "running");
    assert.equal(progress.stats.emails, 2);
    assert.deepEqual(rpc.calls, [
        { method: "run.progress.get", params: { runId: "run-1" } },
        { method: "run.stop", params: { runId: "run-1" } },
    ]);
    assert.equal(stopped.alreadyRequested, true);
});

test("maps mailbox fetch diagnostics and preserves them through legacy progress and terminal events", async () => {
    const diagnostics = [{ uid: 7, reasonCode: "IMAP_MESSAGE_FETCH_FAILED" }];
    const rpc = createRpcClient({
        "run.progress.get": async () => progressSnapshot({ mailboxFetchFailures: diagnostics }),
    });
    const mapped = await runRpc.getProgress(rpc, "run-1");
    assert.deepEqual(mapped.mailbox_fetch_failures, diagnostics);

    const updates = [];
    const feed = runRpc.watchProgress(rpc, "run-1", (value) => updates.push(value));
    await feed.initial;
    rpc.emit("run.progress", {
        runId: "run-1", eventSequence: 1,
        payload: { stage: "archive-documents", percent: 70 },
    });
    rpc.emit("run.terminal", {
        runId: "run-1", eventSequence: 2,
        payload: { runState: "completed", reasonCode: "RUN_COMPLETED" },
    });

    assert.deepEqual(updates.at(-1).mailbox_fetch_failures, diagnostics);
    feed.dispose();

    const legacyRpc = createRpcClient({ "run.progress.get": async () => progressSnapshot() });
    assert.deepEqual((await runRpc.getProgress(legacyRpc, "run-legacy")).mailbox_fetch_failures, []);
});

test("subscribes before snapshot, replays buffered events in sequence order, and ignores duplicates", async () => {
    let resolveSnapshot;
    const rpc = createRpcClient({
        "run.progress.get": () => new Promise((resolve) => { resolveSnapshot = resolve; }),
    });
    const updates = [];
    const feed = runRpc.watchProgress(rpc, "run-1", (value) => updates.push(value));
    assert.equal(rpc.listenerCount("run.progress"), 1);
    assert.equal(rpc.listenerCount("run.terminal"), 1);

    rpc.emit("run.progress", {
        runId: "run-1", eventSequence: 3,
        payload: { stage: "archive-documents", percent: 70 },
    });
    rpc.emit("run.progress", {
        runId: "run-1", eventSequence: 2,
        payload: { stage: "process-documents", percent: 40 },
    });
    resolveSnapshot(progressSnapshot());
    await feed.initial;

    assert.deepEqual(updates.map((item) => item.progress), [10, 40, 70]);
    rpc.emit("run.progress", {
        runId: "run-1", eventSequence: 2,
        payload: { stage: "stale", percent: 5 },
    });
    rpc.emit("run.terminal", {
        runId: "run-1", eventSequence: 4,
        payload: { runState: "completed", reasonCode: "RUN_COMPLETED" },
    });
    assert.equal(updates.at(-1).run_state, "completed");
    assert.equal(updates.at(-1).is_running, false);
    feed.dispose();
    assert.equal(rpc.listenerCount("run.progress"), 0);
    assert.equal(rpc.listenerCount("run.terminal"), 0);
});

test("does not overwrite a newer event with an in-flight reconnect snapshot", async () => {
    let resolveSnapshot;
    const rpc = createRpcClient({
        "run.progress.get": async () => progressSnapshot(),
    });
    const updates = [];
    const feed = runRpc.watchProgress(rpc, "run-1", (value) => updates.push(value));
    await feed.initial;

    rpc.call = (method, params) => {
        rpc.calls.push({ method, params });
        return new Promise((resolve) => { resolveSnapshot = resolve; });
    };
    const refresh = feed.refresh();
    rpc.emit("run.progress", {
        runId: "run-1", eventSequence: 5,
        payload: { stage: "archive-documents", percent: 80 },
    });
    resolveSnapshot(progressSnapshot({ progress: 20, statusText: "stale snapshot" }));
    await refresh;

    assert.equal(updates.at(-1).progress, 80);
    assert.equal(updates.at(-1).status_text, "archive-documents");
    feed.dispose();
});

test("replays buffered events after the initial snapshot reconnects", async () => {
    const rpc = createRpcClient({
        "run.progress.get": async () => { throw new Error("temporarily disconnected"); },
    });
    const updates = [];
    const feed = runRpc.watchProgress(rpc, "run-1", (value) => updates.push(value));
    await assert.rejects(feed.initial, /temporarily disconnected/);
    rpc.emit("run.progress", {
        runId: "run-1", eventSequence: 2,
        payload: { stage: "archive-documents", percent: 70 },
    });

    rpc.call = (method, params) => {
        rpc.calls.push({ method, params });
        return Promise.resolve(progressSnapshot());
    };
    await feed.refresh();

    assert.equal(updates.at(-1).progress, 70);
    feed.dispose();
});

test("adapts typed result fields and exports by run id only", async () => {
    const rpc = createRpcClient({
        "run.results.get": async () => resultData(),
        "run.report.export": async () => ({ runId: "run-1", reportPath: "reports/run-1/report.xlsx", contentHash: "hash" }),
    });

    const results = await runRpc.getResults(rpc, "run-1");
    const report = await runRpc.exportReport(rpc, "run-1");

    assert.equal(results.manual_check_path, "C:/Invoices/archive/run-1/review");
    assert.equal(results.output_path, "C:/Invoices");
    assert.equal(results.last_export_path, "");
    assert.deepEqual(rpc.calls, [
        { method: "run.results.get", params: { runId: "run-1" } },
        { method: "run.report.export", params: { runId: "run-1" } },
    ]);
    assert.equal(report.reportPath, "reports/run-1/report.xlsx");
});

test("routes native folder, report-file, and window operations through fixed typed RPC methods", async () => {
    const rpc = createRpcClient({
        "run.folder.open": async () => ({ succeeded: true }),
        "run.manual-review.open": async () => ({ succeeded: true }),
        "run.file.open": async () => ({ succeeded: true }),
        "window.minimize": async () => ({ succeeded: true }),
    });

    await runRpc.openRunFolder(rpc, "run-1");
    await runRpc.openManualReviewFolder(rpc, "run-1");
    await runRpc.openReport(rpc, "run-1", "reports/run-1/report.xlsx", "hash-1");
    await runRpc.windowCommand(rpc, "minimize");

    assert.deepEqual(rpc.calls, [
        { method: "run.folder.open", params: { runId: "run-1" } },
        { method: "run.manual-review.open", params: { runId: "run-1" } },
        { method: "run.file.open", params: { runId: "run-1", reportPath: "reports/run-1/report.xlsx", contentHash: "hash-1" } },
        { method: "window.minimize", params: null },
    ]);
});

test("settings, processing, and results pages use typed RPC without legacy run calls", () => {
    const legacyMethods = [
        "get_run_context", "start_processing", "get_progress", "stop_processing", "get_results",
        "load_user_settings", "export_run_summary",
    ];
    for (const method of legacyMethods) {
        assert.equal(pageSource.includes(`callApi(\"${method}\"`), false, `legacy run method remains: ${method}`);
    }
    assert.ok(pageSource.includes("RunPageRpc.loadRunContext"));
    assert.ok(pageSource.includes("RunPageRpc.startRun"));
    assert.ok(pageSource.includes("RunPageRpc.watchProgress"));
    assert.ok(pageSource.includes("RunPageRpc.getResults"));
    assert.ok(pageSource.includes("RunPageRpc.exportReport"));
    for (const method of ["RunPageRpc.openRunFolder", "RunPageRpc.openManualReviewFolder", "RunPageRpc.openReport", "RunPageRpc.windowCommand"]) {
        assert.ok(pageSource.includes(method), `missing native action adapter ${method}`);
    }
    assert.equal(pageSource.includes("callApi("), false);
    assert.equal(pageSource.includes("window.pywebview"), false);
    const startHandler = pageSource.slice(
        pageSource.indexOf("async function handleStart()"),
        pageSource.indexOf("if (bootstrapState", pageSource.indexOf("async function handleStart()")));
    assert.ok(startHandler.indexOf("await persistUserSettings") < startHandler.indexOf("const accountId"));
    const canStart = pageSource.slice(pageSource.indexOf("const canStart ="), pageSource.indexOf("useEffect(() => {", pageSource.indexOf("const canStart =")));
    assert.equal(canStart.includes("settings.auth_code"), false);
    assert.equal(canStart.includes("settings.api_key"), false);
});
