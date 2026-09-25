(function (root, factory) {
    "use strict";
    const api = factory();
    if (typeof module === "object" && typeof module.exports === "object") {
        module.exports = api;
    } else {
        root.SettingsRpc = api;
    }
})(typeof window !== "undefined" ? window : globalThis, function () {
    "use strict";

    async function load(rpcClient) {
        const [settings, accounts] = await Promise.all([
            rpcClient.call("settings.get", null),
            rpcClient.call("account.list", null),
        ]);
        return { settings, accounts };
    }

    function saveAccount(rpcClient, account, draft) {
        return rpcClient.call("account.save", {
            account: draft,
            expectedRevision: account ? account.revision : 0,
        });
    }

    function saveSettings(rpcClient, settings, patch) {
        return rpcClient.call("settings.update", {
            expectedRevision: settings.revision,
            ...patch,
        });
    }

    function setSecret(rpcClient, name, value, retention) {
        return rpcClient.call("secret.set", { name, value, retention });
    }

    function deleteSecret(rpcClient, name) {
        return rpcClient.call("secret.delete", { name });
    }

    function testAccount(rpcClient, accountId) {
        return rpcClient.call("account.test", { accountId });
    }

    function testProvider(rpcClient, providerId, credentialName) {
        return rpcClient.call("provider.test", { providerId, credentialName });
    }

    function chooseDirectory(rpcClient) {
        return rpcClient.call("directory.choose", null);
    }

    return {
        load,
        saveAccount,
        saveSettings,
        setSecret,
        deleteSecret,
        testAccount,
        testProvider,
        chooseDirectory,
    };
});