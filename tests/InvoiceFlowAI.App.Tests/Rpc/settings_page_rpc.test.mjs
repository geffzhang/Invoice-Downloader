import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import path from "node:path";

const require = createRequire(import.meta.url);
const settingsRpc = require("../../../templates/settings_rpc.js");

function createRpcClient() {
    const calls = [];
    return {
        calls,
        async call(method, params) {
            calls.push({ method, params });
            if (method === "settings.get") return { revision: 4, companyName: "Buyer" };
            if (method === "account.list") return { items: [] };
            if (method === "settings.update") return { revision: 5, ...params };
            if (method === "account.save") return { ...params.account, revision: 1 };
            if (method === "secret.set") return { name: params.name, configured: true, persistent: params.retention === "persistent" };
            if (method === "secret.delete") return { name: params.name, configured: false, persistent: false };
            if (method === "directory.choose") return { cancelled: false, path: "C:/Invoices" };
            return { success: true, message: "Connected" };
        },
    };
}

test("loads settings and mailbox accounts through typed RPC", async () => {
    const rpc = createRpcClient();
    const loaded = await settingsRpc.load(rpc);

    assert.equal(loaded.settings.revision, 4);
    assert.deepEqual(loaded.accounts.items, []);
    assert.deepEqual(rpc.calls.map(({ method }) => method), ["settings.get", "account.list"]);
});

test("saves revisioned non-secret settings and mailbox account", async () => {
    const rpc = createRpcClient();
    await settingsRpc.saveAccount(rpc, null, {
        accountId: "default-mailbox",
        emailAddress: "buyer@qq.com",
        imapHost: "imap.qq.com",
        imapPort: 993,
        useTls: true,
        credentialName: "mail.imap.auth-code",
        displayName: "buyer@qq.com",
    });
    await settingsRpc.saveSettings(rpc, { revision: 4 }, {
        accountId: "default-mailbox",
        companyName: "Buyer",
        lastOutputDirectory: "C:/Invoices",
    });

    assert.deepEqual(rpc.calls[0], {
        method: "account.save",
        params: {
            account: {
                accountId: "default-mailbox",
                emailAddress: "buyer@qq.com",
                imapHost: "imap.qq.com",
                imapPort: 993,
                useTls: true,
                credentialName: "mail.imap.auth-code",
                displayName: "buyer@qq.com",
            },
            expectedRevision: 0,
        },
    });
    assert.equal(rpc.calls[1].params.expectedRevision, 4);
    assert.equal(rpc.calls[1].params.companyName, "Buyer");
    assert.equal("authCode" in rpc.calls[1].params, false);
    assert.equal("apiKey" in rpc.calls[1].params, false);
});

test("routes secret writes, tests and directory selection without returning secret values", async () => {
    const rpc = createRpcClient();
    const secret = await settingsRpc.setSecret(rpc, "deepseek.api-key", "do-not-return", "session");
    await settingsRpc.testAccount(rpc, "default-mailbox");
    await settingsRpc.testProvider(rpc, "deepseek", "deepseek.api-key");
    const directory = await settingsRpc.chooseDirectory(rpc);

    assert.equal(secret.configured, true);
    assert.equal(JSON.stringify(secret).includes("do-not-return"), false);
    assert.equal(rpc.calls[0].method, "secret.set");
    assert.deepEqual(rpc.calls[1], { method: "account.test", params: { accountId: "default-mailbox" } });
    assert.deepEqual(rpc.calls[2], { method: "provider.test", params: { providerId: "deepseek", credentialName: "deepseek.api-key" } });
    assert.equal(directory.path, "C:/Invoices");
    assert.equal(rpc.calls[3].method, "directory.choose");
});

test("settings UI uses RPC for settings operations and never stores secrets in Web Storage", () => {
    const fs = require("node:fs");
    const sourcePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../templates/index_app.js");
    const source = fs.readFileSync(sourcePath, "utf8");
    const adapterPath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../templates/settings_rpc.js");
    const adapter = fs.readFileSync(adapterPath, "utf8");
    const bootstrapPath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../templates/bridge-bootstrap.js");
    const bootstrap = fs.readFileSync(bootstrapPath, "utf8");
    const persist = source.slice(source.indexOf("async function persistUserSettings"), source.indexOf("function toneFromAsyncStatus"));
    const settingsPage = source.slice(source.indexOf("function SettingsPage"), source.indexOf("function ProcessingPage"));

    for (const method of ["settings.get", "settings.update", "account.list", "account.save", "account.test", "provider.test", "secret.set", "secret.delete", "directory.choose"]) {
        assert.ok(adapter.includes(`\"${method}\"`), `missing RPC method ${method}`);
    }
        assert.match(source, /SettingsRpc\.load\(window\.RpcClient\)/);
        assert.ok(settingsPage.includes("SettingsRpc.chooseDirectory"));
        assert.ok(settingsPage.includes("SettingsRpc.testAccount"));
        assert.ok(settingsPage.includes("SettingsRpc.testProvider"));
        assert.ok(settingsPage.includes("SettingsRpc.setSecret"));
    assert.match(bootstrap, /RpcClient\.handshake\(\)[\s\S]*SettingsRpc\.load\(RpcClient\)/);
    assert.equal(bootstrap.includes("settings.snapshot.get"), false);
    for (const legacyMethod of ["load_user_settings", "save_user_settings", "test_email_auth", "test_connection", "choose_directory"]) {
        assert.equal(settingsPage.includes(legacyMethod), false, `legacy settings method remains on settings page: ${legacyMethod}`);
    }
    assert.ok(/auth_code:\s*""/.test(source));
    assert.ok(/api_key:\s*""/.test(source));
    assert.ok(/setSettings\(\(current\) => \(\{ \.\.\.current, \[settingKey\]: "" \}\)\)/.test(settingsPage));
        assert.ok(settingsPage.includes('"persistent"'));
        assert.ok(settingsPage.includes('"session"'));
        assert.ok(settingsPage.includes('callApi("start_processing"'));
        assert.ok(source.includes('callApi("get_progress"'));
        assert.ok(source.includes('callApi("get_results"'));
    assert.equal(/writeSessionValue\(SESSION_SETTINGS_KEY,\s*\{[^}]*auth_code/.test(persist), false);
    assert.equal(/writeSessionValue\(SESSION_SETTINGS_KEY,\s*\{[^}]*api_key/.test(persist), false);
});