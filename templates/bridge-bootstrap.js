(function () {
    "use strict";

    const indicator = document.getElementById("bridge-status");
    if (!indicator || !window.RpcClient) return;

    function setStatus(message, state) {
        indicator.textContent = message;
        indicator.dataset.state = state;
    }

    setStatus("正在连接桌面服务", "connecting");
    RpcClient.init(window);
    window.invoiceFlowRpcReady = RpcClient.handshake()
        .then(async (info) => ({ info, loaded: await SettingsRpc.load(RpcClient) }))
        .then(({ info, loaded }) => {
            window.invoiceFlowSettingsSnapshot = loaded.settings;
            window.invoiceFlowMailboxAccounts = loaded.accounts.items || [];
            window.invoiceFlowMailboxAccount = window.invoiceFlowMailboxAccounts.find(
                (account) => account.accountId === loaded.settings.currentAccountId) || window.invoiceFlowMailboxAccounts[0] || null;
            setStatus(`桌面服务已连接 · ${info.appVersion}`, "connected");
            indicator.title = `RPC ${info.protocol} · ${info.registeredMethods.join(", ")}`;
            return loaded;
        })
        .catch((error) => {
            setStatus("桌面服务暂不可用", "error");
            throw error;
        });
})();