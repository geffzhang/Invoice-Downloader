// RpcClient — the JS-side counterpart of WebViewRpcBridge. Replaces
// the old `window.pywebview.api` surface with a strict JSON-RPC v1
// client. The client uses `window.chrome.webview.postMessage` to talk
// to the Avalonia host and resolves/rejects promises on response.
//
// The client never uses sessionStorage / localStorage to cache
// anything that looks like a secret (api_key, auth_code, provider
// password). The page must keep those in memory only.
//
// Testability: when loaded as a CommonJS or ES module, the IIFE
// returns the RpcClient object instead of attaching to globalThis.
// In the browser, the script also attaches to window.RpcClient for
// the page to consume.

(function (root, factory) {
    "use strict";
    const api = factory();
    if (typeof module === "object" && typeof module.exports === "object") {
        module.exports = api;
    } else {
        root.RpcClient = api;
    }
})(typeof window !== "undefined" ? window : globalThis, function () {
    "use strict";

    const PROTOCOL = "invoiceflow.rpc.v1";
    const SECRET_KEY_PATTERN = /(api[_-]?key|auth[_-]?code|provider[_-]?secret|password|token)/i;
    const DEFAULT_TIMEOUT_MS = 30000;

    class RpcError extends Error {
        constructor(code, userMessage, details) {
            super(userMessage || code);
            this.code = code;
            this.userMessage = userMessage;
            this.details = details;
        }
    }

    let _nextId = 1;
    const _pending = new Map();
    const _eventHandlers = new Map();
    const _secretMemory = new Map();
    let _root = null;
    let _initCalled = false;
    let _timeoutMs = DEFAULT_TIMEOUT_MS;

    function _generateId() {
        return `req-${Date.now().toString(36)}-${(_nextId++).toString(36)}`;
    }

    function _onHostMessage(event) {
        let envelope;
        try {
            envelope = JSON.parse(event.data);
        } catch (e) {
            return;
        }
        if (!envelope || typeof envelope !== "object") return;
        if (envelope.protocol !== PROTOCOL) return;

        if (envelope.event) {
            const handlers = _eventHandlers.get(envelope.event) || [];
            for (const h of handlers) {
                try { h(envelope); } catch (_) { /* swallow handler errors */ }
            }
            return;
        }

        if (typeof envelope.id !== "string") return;
        const entry = _pending.get(envelope.id);
        if (!entry) return;
        _pending.delete(envelope.id);
        clearTimeout(entry.timer);
        if (envelope.ok) {
            entry.resolve(envelope.result);
        } else {
            const err = envelope.error || {};
            entry.reject(new RpcError(err.code || "RPC_ERROR", err.userMessage, err.details));
        }
    }

    function _post(message) {
        if (_root && typeof _root.invokeCSharpAction === "function") {
            _root.invokeCSharpAction(JSON.stringify(message));
            return;
        }
        if (!_root || !_root.chrome || !_root.chrome.webview || typeof _root.chrome.webview.postMessage !== "function") {
            throw new RpcError("RPC_BACKEND_MISSING", "WebView2 backend is not available", null);
        }
        _root.chrome.webview.postMessage(JSON.stringify(message));
    }

    function call(method, params) {
        return new Promise((resolve, reject) => {
            const id = _generateId();
            const timer = setTimeout(() => {
                if (_pending.delete(id)) {
                    reject(new RpcError("RPC_TIMEOUT", `RPC ${method} timed out`));
                }
            }, _timeoutMs);
            _pending.set(id, { resolve, reject, timer });
            try {
                _post({ protocol: PROTOCOL, id, method, params: params == null ? null : params });
            } catch (err) {
                clearTimeout(timer);
                _pending.delete(id);
                reject(err);
            }
        });
    }

    function on(eventName, handler) {
        if (!_eventHandlers.has(eventName)) _eventHandlers.set(eventName, []);
        _eventHandlers.get(eventName).push(handler);
    }

    function off(eventName, handler) {
        const list = _eventHandlers.get(eventName);
        if (!list) return;
        const idx = list.indexOf(handler);
        if (idx >= 0) list.splice(idx, 1);
    }

    async function handshake() {
        const info = await call("bridge.hello", null);
        if (!info || info.protocol !== PROTOCOL) {
            throw new RpcError("RPC_PROTOCOL_MISMATCH", "host protocol mismatch");
        }
        return info;
    }

    function init(root, options) {
        if (root) _root = root;
        // Do NOT fall through to globalThis when root is undefined —
        // callers may want to re-initialise only the options (e.g. tests
        // that need a short timeoutMs). The first call from page code
        // always passes window explicitly.
        if (options && typeof options.timeoutMs === "number") _timeoutMs = options.timeoutMs;
        if (_initCalled) return;
        _initCalled = true;
        if (!_root) _root = typeof window !== "undefined" ? window : globalThis;
        if (_root.chrome && _root.chrome.webview && _root.chrome.webview.addEventListener) {
            _root.chrome.webview.addEventListener("message", _onHostMessage);
        } else if (_root.addEventListener) {
            _root.addEventListener("message", _onHostMessage);
        }
    }

    function rememberSecret(name, value) {
        if (typeof name !== "string" || SECRET_KEY_PATTERN.test(name)) {
            throw new RpcError("WEB_ASSET_SECRET", `Refusing to cache secret-looking key: ${name}`);
        }
        _secretMemory.set(name, value);
    }

    function recallSecret(name) {
        return _secretMemory.get(name);
    }

    function isSecretKey(name) {
        return SECRET_KEY_PATTERN.test(name);
    }

    // Test hooks (do not call from page code).
    function __resetForTests() {
        _pending.clear();
        _eventHandlers.clear();
        _secretMemory.clear();
        _nextId = 1;
        _initCalled = false;
        _root = null;
        _timeoutMs = DEFAULT_TIMEOUT_MS;
    }

    return {
        PROTOCOL,
        RpcError,
        call,
        on,
        off,
        handshake,
        init,
        rememberSecret,
        recallSecret,
        isSecretKey,
        __resetForTests,
    };
});
