// Verifies the JS-side RpcClient (design §6 / Task 9):
//   * bridge.hello handshake returns the host info
//   * RPC responses are routed back to the right promise
//   * Events are dispatched to registered handlers
//   * Timeout fires when the host does not respond
//   * Unknown events / malformed messages are ignored
//   * sessionStorage is never written with secret-looking keys
//   * Backend-missing throws RPC_BACKEND_MISSING
//
// Runs with `node --test`. The harness loads the CommonJS module,
// installs a fake chrome.webview + sessionStorage, and uses the
// `__resetForTests` hook to reset module state between tests.

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const clientSrc = fs.readFileSync(
    path.resolve(here, "../../../templates/rpc_client.js"),
    "utf8");

function loadClient() {
    // Evaluate the client source fresh so module-level state is
    // independent per test. The IIFE writes to module.exports if
    // `module` is an object, so we hand it a sandbox module to
    // capture the result without polluting the real Node module.
    const bus = { listeners: new Set(), posted: [] };
    const sessionStorageData = new Map();
    const fakeWindow = {
        chrome: {
            webview: {
                postMessage(msg) { bus.posted.push(JSON.parse(msg)); },
                addEventListener(_evt, fn) { bus.listeners.add(fn); },
            },
        },
        sessionStorage: {
            getItem: (k) => sessionStorageData.has(k) ? sessionStorageData.get(k) : null,
            setItem: (k, v) => { sessionStorageData.set(k, v); },
            removeItem: (k) => { sessionStorageData.delete(k); },
        },
        addEventListener(_evt, fn) { bus.listeners.add(fn); },
    };
    const sandboxModule = { exports: {} };
    // The IIFE's wrapper checks `typeof module === "object"` — we
    // shadow `module` in the script scope by passing it as a
    // parameter to a fresh Function. Inside the script, `module` is
    // our sandbox, so `module.exports = api` lands in the sandbox.
    new Function("module", "exports", clientSrc)(sandboxModule, sandboxModule.exports);
    const RpcClient = sandboxModule.exports;
    if (typeof RpcClient.__resetForTests === "function") RpcClient.__resetForTests();
    RpcClient.init(fakeWindow);
    return { RpcClient, bus, sessionStorage: sessionStorageData };
}

function reply(bus, id, ok, payload) {
    const msg = { protocol: "invoiceflow.rpc.v1", id, ok };
    if (ok) msg.result = payload;
    else msg.error = payload;
    for (const fn of bus.listeners) fn({ data: JSON.stringify(msg) });
}

test("handshake returns host info and checks protocol", async () => {
    const { RpcClient, bus } = loadClient();
    const promise = RpcClient.handshake();
    const pending = bus.posted.shift();
    assert.equal(pending.method, "bridge.hello");
    reply(bus, pending.id, true, {
        appName: "InvoiceFlowAI", appVersion: "1.0.0",
        backendKind: "WebView2", protocol: "invoiceflow.rpc.v1",
        registeredMethods: ["bridge.hello"],
    });
    const info = await promise;
    assert.equal(info.appName, "InvoiceFlowAI");
    assert.equal(info.protocol, "invoiceflow.rpc.v1");
});

test("call resolves on ok response and rejects on error", async () => {
    const { RpcClient, bus } = loadClient();
    const promise = RpcClient.call("settings.get", { name: "save_path" });
    const pending = bus.posted.shift();
    assert.equal(pending.method, "settings.get");
    assert.equal(pending.protocol, "invoiceflow.rpc.v1");
    reply(bus, pending.id, true, { savePath: "C:/output" });
    const result = await promise;
    assert.equal(result.savePath, "C:/output");

    const failing = RpcClient.call("settings.get", { name: "missing" });
    const pending2 = bus.posted.shift();
    reply(bus, pending2.id, false, { code: "BAD", userMessage: "not found" });
    await assert.rejects(failing, (err) => err.code === "BAD");
});

test("events are dispatched to registered handlers", () => {
    const { RpcClient, bus } = loadClient();
    const seen = [];
    RpcClient.on("run.progress", (env) => seen.push(env));
    for (const fn of bus.listeners) {
        fn({ data: JSON.stringify({
            protocol: "invoiceflow.rpc.v1",
            event: "run.progress",
            runId: "run-1",
            eventSequence: 1,
            emittedAtUtc: "2026-09-23T12:00:00Z",
            payload: { progress: 0.5 },
        }) });
    }
    assert.equal(seen.length, 1);
    assert.equal(seen[0].payload.progress, 0.5);
});

test("unknown events and malformed messages are ignored", () => {
    const { RpcClient, bus } = loadClient();
    let called = false;
    RpcClient.on("run.progress", () => { called = true; });
    for (const fn of bus.listeners) {
        fn({ data: JSON.stringify({ protocol: "invoiceflow.rpc.v1", event: "other.event" }) });
        fn({ data: "not json" });
        fn({ data: JSON.stringify({ protocol: "wrong" }) });
    }
    assert.equal(called, false);
});

test("timeout fires when the host does not respond", async () => {
    const { RpcClient, bus } = loadClient();
    RpcClient.init(undefined, { timeoutMs: 30 });
    // Drain the post queue so the test message goes out; the host
    // never responds, so the timeout must fire.
    const promise = RpcClient.call("slow", null);
    bus.posted.shift();
    await assert.rejects(promise, (err) => err.code === "RPC_TIMEOUT");
});

test("backend missing throws RPC_BACKEND_MISSING", async () => {
    const { RpcClient } = loadClient();
    const emptyWindow = { addEventListener() {} };
    RpcClient.init(emptyWindow);
    await assert.rejects(
        RpcClient.call("ping", null),
        (err) => err.code === "RPC_BACKEND_MISSING");
});

test("secret-looking keys are not stored", () => {
    const { RpcClient, sessionStorage } = loadClient();
    for (const name of ["api_key", "auth_code", "provider_secret", "PASSWORD", "Token", "apikey"]) {
        assert.throws(
            () => RpcClient.rememberSecret(name, "value"),
            (err) => err.code === "WEB_ASSET_SECRET",
            `should reject ${name}`);
    }
    RpcClient.rememberSecret("save_path", "C:/output");
    assert.equal(RpcClient.recallSecret("save_path"), "C:/output");
    assert.equal(sessionStorage.size, 0);
});

test("isSecretKey recognises secret patterns", () => {
    const { RpcClient } = loadClient();
    assert.equal(RpcClient.isSecretKey("api_key"), true);
    assert.equal(RpcClient.isSecretKey("apiKey"), true);
    assert.equal(RpcClient.isSecretKey("auth_code"), true);
    assert.equal(RpcClient.isSecretKey("password"), true);
    assert.equal(RpcClient.isSecretKey("token"), true);
    assert.equal(RpcClient.isSecretKey("save_path"), false);
    assert.equal(RpcClient.isSecretKey("run_id"), false);
});
