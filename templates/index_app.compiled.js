function _extends() { _extends = Object.assign ? Object.assign.bind() : function (target) { for (var i = 1; i < arguments.length; i++) { var source = arguments[i]; for (var key in source) { if (Object.prototype.hasOwnProperty.call(source, key)) { target[key] = source[key]; } } } return target; }; return _extends.apply(this, arguments); }
const {
  useEffect,
  useMemo,
  useRef,
  useState
} = React;
const {
  createRoot
} = ReactDOM;
const {
  MemoryRouter,
  Routes,
  Route,
  useNavigate
} = ReactRouterDOM;
function isoDate(date) {
  const year = date.getFullYear();
  const month = `${date.getMonth() + 1}`.padStart(2, "0");
  const day = `${date.getDate()}`.padStart(2, "0");
  return `${year}-${month}-${day}`;
}
function shiftDays(days) {
  const date = new Date();
  date.setDate(date.getDate() + days);
  return date;
}
function lastDaysRange(days) {
  return {
    date_from: isoDate(shiftDays(-(days - 1))),
    date_to: isoDate(new Date())
  };
}
const DEFAULT_SETTINGS = {
  email: "",
  auth_code: "",
  api_key: "",
  save_path: "",
  company: "",
  remember_settings: true
};
const DEFAULT_RUN_SETTINGS = {
  ...lastDaysRange(30),
  quick_range: "last_30_days"
};
const DEFAULT_PROGRESS = {
  progress: 0,
  status_text: "等待任务开始...",
  logs: [],
  stats: {
    emails: 0,
    invoices: 0,
    errors: 0
  },
  is_running: false,
  run_state: "idle",
  last_error: "",
  stop_requested: false,
  can_stop: false,
  quota_exhausted: false,
  quota_message: "",
  build_identity: null
};
const APP_BRAND = {
  name: "InvoiceFlowAI",
  subtitle: "AI发票管家"
};
const APP_VISIBLE_COPY = {
  topSubtitle: "AI发票管家",
  footerVersion: "InvoiceFlowAI",
  footerStamp: "P-H-Dx",
  githubLabel: "产品官网",
  githubUrl: "https://github.com/EthanYoQ/Invoice-Downloader"
};
const DEEPSEEK_API_KEYS_URL = "https://platform.deepseek.com/api_keys";
const EMAIL_DOMAIN_OPTIONS = [{
  value: "qq.com",
  label: "qq.com"
}, {
  value: "163.com",
  label: "163.com"
}];
const DISCLAIMER_SOFTWARE_ITEMS = ["本软件用于票据整理、归档与复核辅助处理。", "本软件不附带任何真实邮箱凭据或 API Key。", "请仅填写并使用你自己的邮箱授权信息与 API Key。", "涉及报销、入账或合规判断的重要票据，请务必进行人工复核。"];
const DISCLAIMER_ITEMS = ["本工具用于票据整理与归档辅助，识别结果应结合业务流程进行复核。", "对于识别、归档、遗漏、误判及由此产生的后果，工具方不承担责任。", "使用者应自行遵守所在公司关于大模型、API 使用和数据安全管理的规定。", "涉及报销、入账或合规判断的重要票据，请务必进行人工复核。"];
const UI_COPY = {
  shell: {
    close: "关闭程序",
    closing: "正在关闭...",
    closeFailed: "关闭程序失败，请稍后重试。",
    minimize: "最小化窗口",
    minimizing: "正在最小化...",
    minimizeFailed: "最小化窗口失败，请稍后重试。",
    maximize: "最大化窗口",
    maximizing: "正在最大化...",
    maximizeFailed: "最大化窗口失败，请稍后重试。",
    windowSubtitle: "桌面工作区"
  },
  navigation: [{
    key: "settings",
    label: "启动配置",
    description: "邮箱、API 与日期",
    icon: "tune",
    path: "/"
  }, {
    key: "processing",
    label: "处理中心",
    description: "进度与日志",
    icon: "data_thresholding",
    path: "/processing"
  }, {
    key: "analysis",
    label: "结果分析",
    description: "总览与导出",
    icon: "assessment",
    path: "/analysis"
  }],
  pages: {
    settings: {
      eyebrow: "Step 1",
      title: "启动配置",
      footerText: "当前页: 启动配置",
      bootstrapTitle: "启动配置",
      bootstrapDescription: "正在初始化本地设置。",
      bootstrapMessage: "正在连接桌面接口并加载本地设置，请稍候。",
      errorDescription: "本地设置初始化失败。",
      controlledNotice: "受控复跑已锁定输出目录与日期窗口。"
    },
    processing: {
      eyebrow: "Step 2",
      title: "处理中心",
      footerText: "当前页: 处理中心",
      closeHint: "关闭程序会直接结束当前窗口与任务进程，请仅在确认后执行。",
      statusWaiting: "等待任务状态",
      currentOperation: "当前操作",
      progressLabel: "完成度",
      liveRefresh: "进度与日志保持实时刷新。",
      stopPending: "已收到安全停止指令。",
      stopDetail: "系统将在当前文件处理完成后安全停止。",
      processingDetail: "邮件抓取、票据恢复与结构化识别按既有后端流程执行。",
      stopNotice: "已收到安全停止指令，当前邮件或当前文件处理完成后将结束任务。"
    },
    analysis: {
      eyebrow: "Step 3",
      title: "结果分析",
      footerText: "当前页: 结果分析",
      statusSummary: "运行结果",
      resultTitle: "本次处理完成",
      reviewTitle: "待人工复核",
      reviewEmpty: "当前没有待人工复核记录。",
      reviewReady: "请优先前往待人工复核文件夹处理这些记录。",
      reviewIdle: "结果明细仍可导出查看，输出目录会保留本轮成功归档结果。"
    }
  }
};
function joinClasses(...values) {
  return values.filter(Boolean).join(" ");
}
function openExternalUrl(url) {
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.target = "_blank";
  anchor.rel = "noopener noreferrer";
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
}
function validateEmail(email) {
  const value = String(email || "").trim();
  if (!value) {
    return "请输入邮箱地址。";
  }
  if (!/@(qq|163)\.com$/i.test(value)) {
    return "当前支持 QQ 邮箱和 163 邮箱。";
  }
  return "";
}
function validateDateRange(dateFrom, dateTo) {
  const pattern = /^\d{4}-\d{2}-\d{2}$/;
  if (!pattern.test(String(dateFrom || ""))) {
    return "开始日期格式必须为 YYYY-MM-DD。";
  }
  if (!pattern.test(String(dateTo || ""))) {
    return "结束日期格式必须为 YYYY-MM-DD。";
  }
  if (dateFrom > dateTo) {
    return "开始日期不能晚于结束日期。";
  }
  return "";
}
function maskEmail(email) {
  const value = String(email || "");
  const [name, domain] = value.split("@");
  if (!name || !domain) {
    return value || "--";
  }
  if (name.length <= 2) {
    return `${name[0] || ""}***@${domain}`;
  }
  return `${name.slice(0, 2)}***@${domain}`;
}
function safeText(value, fallback = "--") {
  return value === undefined || value === null || value === "" ? fallback : String(value);
}
function preferNonEmpty(...values) {
  for (const value of values) {
    if (value !== undefined && value !== null && String(value).trim() !== "") {
      return value;
    }
  }
  return "";
}
function fileNameFromPath(path, fallback = "未命名文件") {
  const value = String(path || "");
  if (!value) {
    return fallback;
  }
  const parts = value.split(/[/\\]/);
  return parts[parts.length - 1] || fallback;
}
function parentFolder(path) {
  const value = String(path || "");
  if (!value) {
    return "";
  }
  return value.replace(/[/\\][^/\\]+$/, "");
}
const SESSION_SETTINGS_KEY = "invoiceflow.session.settings";
const SESSION_RUN_SETTINGS_KEY = "invoiceflow.session.runSettings";
const SESSION_ACTIVE_RUN_KEY = "invoiceflow.session.activeRun";
const CONTROLLED_AUTOSTART_PREFIX = "invoiceflow.controlledAutostart";
function readSessionValue(key) {
  try {
    const raw = window.sessionStorage.getItem(key);
    return raw ? JSON.parse(raw) : {};
  } catch (error) {
    return {};
  }
}
function writeSessionValue(key, value) {
  try {
    window.sessionStorage.setItem(key, JSON.stringify(value || {}));
  } catch (error) {
    console.warn("Failed to write session state", error);
  }
}
function hasExplicitQaRunContext(runContext) {
  return !!(runContext && runContext.explicit_run_context && runContext.controlled_run);
}
function shouldAutostartControlledRun(runContext) {
  return !!(hasExplicitQaRunContext(runContext) && runContext.autostart_enabled);
}
function controlledAutostartStateKey(runContext) {
  const token = safeText(runContext && runContext.autostart_token || "", "").trim();
  const runId = safeText(runContext && runContext.run_id || "", "").trim();
  return [CONTROLLED_AUTOSTART_PREFIX, token || runId || "default"].join(".");
}
function markControlledAutostartConsumed(runContext) {
  writeSessionValue(controlledAutostartStateKey(runContext), {
    consumed: true,
    consumed_at: new Date().toISOString()
  });
}
function isControlledAutostartConsumed(runContext) {
  const payload = readSessionValue(controlledAutostartStateKey(runContext));
  return !!payload.consumed;
}
function buildPersistPayload(settings, runSettings, runContext) {
  const payload = {
    ...DEFAULT_SETTINGS,
    ...DEFAULT_RUN_SETTINGS,
    ...(runSettings || {}),
    ...(settings || {})
  };
  if (hasExplicitQaRunContext(runContext)) {
    if (runContext.locked_output_path) {
      payload.save_path = runContext.locked_output_path;
    }
    if (runContext.locked_date_from) {
      payload.date_from = runContext.locked_date_from;
    }
    if (runContext.locked_date_to) {
      payload.date_to = runContext.locked_date_to;
    }
  }
  return payload;
}
async function loadShellState() {
  const [loaded, runContextRes] = await Promise.all([window.invoiceFlowRpcReady || SettingsRpc.load(window.RpcClient), RunPageRpc.loadRunContext(window.RpcClient).catch(() => ({}))]);
  const snapshot = loaded.settings;
  const account = window.invoiceFlowMailboxAccount || (loaded.accounts.items || [])[0] || null;
  const sessionSettings = readSessionValue(SESSION_SETTINGS_KEY);
  const sessionRunSettings = readSessionValue(SESSION_RUN_SETTINGS_KEY);
  const runContext = runContextRes || {};
  const settings = {
    email: preferNonEmpty(sessionSettings.email, account && account.emailAddress),
    auth_code: "",
    api_key: "",
    save_path: preferNonEmpty(sessionSettings.save_path, snapshot.lastOutputDirectory),
    company: preferNonEmpty(sessionSettings.company, snapshot.companyName),
    remember_settings: sessionSettings.remember_settings !== false
  };
  const runSettings = {
    date_from: preferNonEmpty(sessionRunSettings.date_from, DEFAULT_RUN_SETTINGS.date_from),
    date_to: preferNonEmpty(sessionRunSettings.date_to, DEFAULT_RUN_SETTINGS.date_to),
    quick_range: preferNonEmpty(sessionRunSettings.quick_range, DEFAULT_RUN_SETTINGS.quick_range)
  };
  if (hasExplicitQaRunContext(runContext)) {
    if (runContext.locked_email) {
      settings.email = runContext.locked_email;
    }
    if (runContext.locked_output_path) {
      settings.save_path = runContext.locked_output_path;
    }
    if (runContext.locked_date_from) {
      runSettings.date_from = runContext.locked_date_from;
    }
    if (runContext.locked_date_to) {
      runSettings.date_to = runContext.locked_date_to;
    }
  }
  window.invoiceFlowRememberSettings = settings.remember_settings;
  return {
    settings,
    runSettings,
    runContext,
    needsInitialSetup: SettingsRpc.needsInitialSetup(loaded)
  };
}
let settingsWriteQueue = Promise.resolve();
async function persistUserSettings(settings, runSettings, runContext) {
  const persist = async () => {
    const payload = buildPersistPayload(settings, runSettings, runContext);
    const {
      auth_code: _authCode,
      api_key: _apiKey,
      ...safePayload
    } = payload;
    writeSessionValue(SESSION_SETTINGS_KEY, {
      email: safePayload.email || "",
      save_path: safePayload.save_path || "",
      company: safePayload.company || "",
      remember_settings: safePayload.remember_settings !== false
    });
    writeSessionValue(SESSION_RUN_SETTINGS_KEY, {
      date_from: safePayload.date_from || "",
      date_to: safePayload.date_to || "",
      quick_range: safePayload.quick_range || DEFAULT_RUN_SETTINGS.quick_range
    });
    const remember = safePayload.remember_settings !== false;
    if (window.invoiceFlowRememberSettings !== remember) {
      const inMemorySecrets = window.invoiceFlowRunSecrets || {};
      const retentionUpdates = [["mail.imap.auth-code", inMemorySecrets.auth_code], ["deepseek.api-key", inMemorySecrets.api_key]].map(([name, value]) => value ? SettingsRpc.setSecret(window.RpcClient, name, value, remember ? "persistent" : "session") : remember ? Promise.resolve() : SettingsRpc.deleteSecret(window.RpcClient, name));
      await Promise.all(retentionUpdates);
    }
    window.invoiceFlowRememberSettings = remember;
    const email = String(safePayload.email || "").trim();
    let account = window.invoiceFlowMailboxAccount || null;
    if (email) {
      const is163 = email.toLowerCase().endsWith("@163.com");
      const draft = {
        accountId: account ? account.accountId : "default-mailbox",
        emailAddress: email,
        imapHost: is163 ? "imap.163.com" : "imap.qq.com",
        imapPort: 993,
        useTls: true,
        credentialName: "mail.imap.auth-code",
        displayName: email,
        defaultMailbox: "INBOX"
      };
      const unchanged = account && Object.keys(draft).every(key => account[key] === draft[key]);
      if (!unchanged) {
        account = await SettingsRpc.saveAccount(window.RpcClient, account, draft);
        window.invoiceFlowMailboxAccount = account;
      }
    }
    const snapshot = window.invoiceFlowSettingsSnapshot;
    const companyName = remember ? String(safePayload.company || "") : "";
    const lastOutputDirectory = remember ? safePayload.save_path || null : null;
    const accountId = account ? account.accountId : snapshot.currentAccountId;
    if (snapshot.companyName !== companyName || snapshot.lastOutputDirectory !== lastOutputDirectory || snapshot.currentAccountId !== accountId) {
      window.invoiceFlowSettingsSnapshot = await SettingsRpc.saveSettings(window.RpcClient, snapshot, {
        accountId,
        companyName,
        lastOutputDirectory
      });
    }
  };
  const pending = settingsWriteQueue.then(persist);
  settingsWriteQueue = pending.catch(() => {});
  return pending;
}
function toneFromAsyncStatus(status) {
  if (status === "success") return "success";
  if (status === "error") return "error";
  if (status === "testing") return "info";
  return "info";
}
function splitEmailAddress(email) {
  const value = String(email || "").trim();
  const [username = "", rawDomain = ""] = value.split("@");
  const domain = rawDomain === "163.com" ? "163.com" : "qq.com";
  return {
    username,
    domain
  };
}
function resolveGroupTone(groupKey) {
  if (groupKey === "manual_review") return "group-chip--warning";
  if (groupKey === "retained_record") return "group-chip--info";
  if (groupKey === "processing_error") return "group-chip--error";
  return "";
}
function resolveLogToneClass(log) {
  const sample = `${String(log && log.color || "")} ${String(log && log.type || "")}`.toLowerCase();
  if (sample.includes("error") || sample.includes("red")) return "terminal-kind terminal-kind--error";
  if (sample.includes("warning") || sample.includes("warn") || sample.includes("amber") || sample.includes("orange")) return "terminal-kind terminal-kind--warning";
  if (sample.includes("success") || sample.includes("green") || sample.includes("emerald")) return "terminal-kind terminal-kind--success";
  if (sample.includes("info") || sample.includes("blue") || sample.includes("cyan")) return "terminal-kind terminal-kind--info";
  return "terminal-kind";
}
function NoticeBox({
  tone = "info",
  className = "",
  children
}) {
  return React.createElement("div", {
    className: joinClasses("notice", `notice--${tone}`, className)
  }, children);
}
function InlineStatus({
  tone = "info",
  children
}) {
  const icon = tone === "success" ? "task_alt" : tone === "error" ? "error" : tone === "warning" ? "warning" : "info";
  return React.createElement("div", {
    className: joinClasses("inline-status", `inline-status--${tone}`)
  }, React.createElement("span", {
    className: "material-symbols-outlined inline-status__icon"
  }, icon), React.createElement("span", {
    className: "inline-status__text"
  }, children));
}
function StatusPill({
  tone = "neutral",
  icon,
  children
}) {
  return React.createElement("span", {
    className: joinClasses("status-pill", tone !== "neutral" && `status-pill--${tone}`)
  }, icon && React.createElement("span", {
    className: "material-symbols-outlined",
    style: {
      fontSize: 16
    }
  }, icon), React.createElement("span", null, children));
}
function SectionHeader({
  icon,
  title,
  indicator
}) {
  return React.createElement("div", {
    className: "section-header"
  }, React.createElement("div", {
    className: "section-title"
  }, React.createElement("span", {
    className: "section-title__icon material-symbols-outlined"
  }, icon), React.createElement("span", null, title)), indicator ? React.createElement("div", {
    className: "u-text-muted",
    style: {
      fontSize: 12
    }
  }, indicator) : null);
}
function PageHeader({
  eyebrow,
  title,
  description,
  badge
}) {
  return React.createElement("div", {
    className: "page-header-shell"
  }, React.createElement("div", null, eyebrow ? React.createElement("p", {
    className: "page-eyebrow"
  }, eyebrow) : null, React.createElement("h1", {
    className: "page-title"
  }, title), description ? React.createElement("p", {
    className: "page-description"
  }, description) : null), badge ? React.createElement("div", {
    className: "page-header-badge"
  }, badge) : null);
}
function PageFooter({
  left,
  right
}) {
  return React.createElement("footer", {
    className: "footer-bar"
  }, React.createElement("div", {
    className: "footer-slot"
  }, left), React.createElement("div", {
    className: "footer-slot footer-slot--right"
  }, right));
}
function DarkSelect({
  value,
  options,
  onChange,
  disabled = false,
  ariaLabel
}) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef(null);
  const activeOption = options.find(option => option.value === value) || options[0] || {
    value: "",
    label: ""
  };
  useEffect(() => {
    if (!open) return undefined;
    function handlePointerDown(event) {
      if (rootRef.current && !rootRef.current.contains(event.target)) {
        setOpen(false);
      }
    }
    function handleKeyDown(event) {
      if (event.key === "Escape") {
        setOpen(false);
      }
    }
    window.addEventListener("mousedown", handlePointerDown);
    window.addEventListener("keydown", handleKeyDown);
    return () => {
      window.removeEventListener("mousedown", handlePointerDown);
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [open]);
  function handleSelect(nextValue) {
    onChange(nextValue);
    setOpen(false);
  }
  return React.createElement("div", {
    ref: rootRef,
    className: joinClasses("field-shell", "field-shell--dropdown", disabled && "field-shell--readonly", open && "field-shell--dropdown-open")
  }, React.createElement("button", {
    type: "button",
    className: "field-dropdown-trigger",
    "aria-haspopup": "listbox",
    "aria-expanded": open,
    "aria-label": ariaLabel,
    onClick: () => !disabled && setOpen(current => !current),
    disabled: disabled
  }, React.createElement("span", {
    className: "field-dropdown-trigger__label"
  }, activeOption.label), React.createElement("span", {
    className: "material-symbols-outlined field-dropdown-trigger__icon"
  }, open ? "expand_less" : "expand_more")), open ? React.createElement("div", {
    className: "field-dropdown-menu",
    role: "listbox",
    "aria-label": ariaLabel
  }, options.map(option => React.createElement("button", {
    key: option.value,
    type: "button",
    className: joinClasses("field-dropdown-option", option.value === activeOption.value && "is-active"),
    onClick: () => handleSelect(option.value)
  }, React.createElement("span", null, option.label), option.value === activeOption.value ? React.createElement("span", {
    className: "material-symbols-outlined"
  }, "check") : null))) : null);
}
function DateField({
  label,
  value,
  onChange,
  disabled = false
}) {
  const inputRef = useRef(null);
  function openPicker() {
    if (disabled || !inputRef.current) return;
    if (typeof inputRef.current.showPicker === "function") {
      try {
        inputRef.current.showPicker();
        return;
      } catch (error) {}
    }
    inputRef.current.focus();
    inputRef.current.click();
  }
  return React.createElement("div", {
    className: "field-block",
    style: {
      flex: 1
    }
  }, React.createElement("label", {
    className: "field-label"
  }, label), React.createElement("div", {
    className: joinClasses("field-shell", "field-shell--date", disabled && "field-shell--readonly")
  }, React.createElement("input", {
    ref: inputRef,
    type: "date",
    className: "field-input field-input--date",
    value: value,
    onChange: event => onChange(event.target.value),
    onClick: openPicker,
    disabled: disabled
  }), React.createElement("button", {
    type: "button",
    className: "field-shell-button",
    onClick: openPicker,
    disabled: disabled,
    "aria-label": `${label}日历`
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "calendar_month"))));
}
function AppWindowChrome({
  active
}) {
  const [closing, setClosing] = useState(false);
  const [minimizing, setMinimizing] = useState(false);
  const [maximizing, setMaximizing] = useState(false);
  const activeItem = UI_COPY.navigation.find(item => item.key === active);
  async function handleMinimize() {
    if (minimizing) return;
    setMinimizing(true);
    try {
      const result = await RunPageRpc.windowCommand(window.RpcClient, "minimize");
      if (!result || !result.succeeded) {
        throw new Error(result && result.message || UI_COPY.shell.minimizeFailed);
      }
    } catch (error) {
      window.alert(error.message || UI_COPY.shell.minimizeFailed);
    } finally {
      setMinimizing(false);
    }
  }
  async function handleMaximize() {
    if (maximizing) return;
    setMaximizing(true);
    try {
      const result = await RunPageRpc.windowCommand(window.RpcClient, "maximize");
      if (!result || !result.succeeded) {
        throw new Error(result && result.message || UI_COPY.shell.maximizeFailed);
      }
    } catch (error) {
      window.alert(error.message || UI_COPY.shell.maximizeFailed);
    } finally {
      setMaximizing(false);
    }
  }
  async function handleClose() {
    if (closing) return;
    setClosing(true);
    try {
      const result = await RunPageRpc.windowCommand(window.RpcClient, "close");
      if (!result || !result.succeeded) {
        throw new Error(result && result.message || UI_COPY.shell.closeFailed);
      }
    } catch (error) {
      setClosing(false);
      window.alert(error.message || UI_COPY.shell.closeFailed);
    }
  }
  return React.createElement("div", {
    className: "window-chrome"
  }, React.createElement("div", {
    className: "window-drag-region"
  }, React.createElement("div", {
    className: "window-title-stack"
  }, React.createElement("span", {
    className: "window-title"
  }, APP_BRAND.name), React.createElement("span", {
    className: "window-subtitle"
  }, activeItem && activeItem.label || UI_COPY.shell.windowSubtitle))), React.createElement("div", {
    className: "window-controls"
  }, React.createElement("button", {
    type: "button",
    className: "window-traffic-button window-traffic-button--minimize",
    onClick: handleMinimize,
    disabled: minimizing || maximizing || closing,
    "aria-label": minimizing ? UI_COPY.shell.minimizing : UI_COPY.shell.minimize,
    title: minimizing ? UI_COPY.shell.minimizing : UI_COPY.shell.minimize
  }, React.createElement("span", {
    className: "window-traffic-button__glyph"
  })), React.createElement("button", {
    type: "button",
    className: "window-traffic-button window-traffic-button--maximize",
    onClick: handleMaximize,
    disabled: minimizing || maximizing || closing,
    "aria-label": maximizing ? UI_COPY.shell.maximizing : UI_COPY.shell.maximize,
    title: maximizing ? UI_COPY.shell.maximizing : UI_COPY.shell.maximize
  }, React.createElement("span", {
    className: "window-traffic-button__glyph"
  })), React.createElement("button", {
    type: "button",
    className: "window-traffic-button window-traffic-button--close",
    onClick: handleClose,
    disabled: minimizing || maximizing || closing,
    "aria-label": closing ? UI_COPY.shell.closing : UI_COPY.shell.close,
    title: closing ? UI_COPY.shell.closing : UI_COPY.shell.close
  }, React.createElement("span", {
    className: "window-traffic-button__glyph"
  }))));
}
function AppShell({
  active,
  onOpenDisclaimer,
  children,
  footerLeft,
  footerRight,
  contentClassName = "",
  contentScrollable = true,
  initialSetup = false
}) {
  return React.createElement("div", {
    className: joinClasses("app-shell", initialSetup && "app-shell--initial-setup")
  }, React.createElement(Sidebar, {
    active: active,
    onOpenDisclaimer: onOpenDisclaimer
  }), React.createElement("main", {
    className: "app-main"
  }, React.createElement(AppWindowChrome, {
    active: active
  }), React.createElement("div", {
    className: joinClasses("page-scroll", !contentScrollable && "page-scroll--locked", contentClassName)
  }, children), React.createElement(PageFooter, {
    left: footerLeft,
    right: footerRight
  })));
}
function BootstrapStatePage({
  active,
  onOpenDisclaimer,
  eyebrow,
  title,
  description,
  tone,
  message,
  footerText
}) {
  return React.createElement(AppShell, {
    active: active,
    onOpenDisclaimer: onOpenDisclaimer,
    footerRight: React.createElement("p", {
      className: "footer-meta"
    }, footerText)
  }, React.createElement("div", {
    className: "page-wrap page-wrap--narrow"
  }, React.createElement(PageHeader, {
    eyebrow: eyebrow,
    title: title,
    description: description
  }), React.createElement(NoticeBox, {
    tone: tone
  }, message)));
}
function SummaryField({
  span = 6,
  label,
  value,
  helper,
  icon,
  tone = "",
  mono = false,
  truncate = false
}) {
  return React.createElement("div", {
    className: joinClasses("summary-item", tone),
    style: {
      gridColumn: `span ${span} / span ${span}`
    }
  }, React.createElement("div", {
    className: "summary-item__icon"
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, icon)), React.createElement("div", {
    className: "summary-item__copy"
  }, React.createElement("p", {
    className: "summary-item__label"
  }, label), React.createElement("p", {
    className: joinClasses("summary-item__value", mono && "u-mono", truncate && "u-truncate")
  }, value), helper ? React.createElement("p", {
    className: "summary-item__helper"
  }, helper) : null));
}
function DisclaimerDialog({
  open,
  onClose
}) {
  useEffect(() => {
    if (!open) return undefined;
    function handleKeydown(event) {
      if (event.key === "Escape") onClose();
    }
    window.addEventListener("keydown", handleKeydown);
    return () => window.removeEventListener("keydown", handleKeydown);
  }, [open, onClose]);
  if (!open) return null;
  return React.createElement("div", {
    className: "modal-overlay",
    onClick: onClose
  }, React.createElement("div", {
    className: "modal-card",
    onClick: event => event.stopPropagation()
  }, React.createElement("div", {
    className: "modal-head"
  }, React.createElement("div", null, React.createElement("p", {
    className: "page-eyebrow",
    style: {
      marginBottom: 10
    }
  }, "\u514D\u8D23\u58F0\u660E"), React.createElement("h2", {
    className: "page-title",
    style: {
      fontSize: 28
    }
  }, "\u4F7F\u7528\u524D\u8BF7\u786E\u8BA4\u4EE5\u4E0B\u4E8B\u9879")), React.createElement("button", {
    type: "button",
    className: "btn btn--ghost btn--sm",
    onClick: onClose
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "close"))), React.createElement("div", {
    className: "modal-body"
  }, React.createElement("section", {
    className: "modal-section"
  }, React.createElement("p", {
    className: "modal-label"
  }, "\u8F6F\u4EF6\u8BF4\u660E"), React.createElement("ul", {
    className: "modal-list"
  }, DISCLAIMER_SOFTWARE_ITEMS.map(item => React.createElement("li", {
    key: item
  }, item)))), React.createElement("section", {
    className: "modal-section"
  }, React.createElement("p", {
    className: "modal-label"
  }, "\u98CE\u9669\u63D0\u793A"), React.createElement("ul", {
    className: "modal-list"
  }, DISCLAIMER_ITEMS.map(item => React.createElement("li", {
    key: item
  }, item))))), React.createElement("div", {
    className: "modal-footer"
  }, React.createElement("button", {
    type: "button",
    className: "btn btn--primary",
    onClick: onClose
  }, "\u6211\u5DF2\u77E5\u6653"))));
}
function Sidebar({
  active,
  onOpenDisclaimer
}) {
  const navigate = useNavigate();
  const githubUrl = APP_VISIBLE_COPY.githubUrl;
  return React.createElement("aside", {
    className: "app-sidebar"
  }, React.createElement("div", {
    className: "sidebar-top"
  }, React.createElement("div", {
    className: "sidebar-logo"
  }, "IF"), React.createElement("div", null, React.createElement("p", {
    className: "sidebar-title"
  }, APP_BRAND.name), React.createElement("p", {
    className: "sidebar-subtitle"
  }, APP_VISIBLE_COPY.topSubtitle))), React.createElement("nav", {
    className: "sidebar-nav"
  }, UI_COPY.navigation.map(item => React.createElement("button", {
    key: item.key,
    type: "button",
    className: joinClasses("sidebar-nav__item", active === item.key && "is-active"),
    onClick: () => navigate(item.path)
  }, React.createElement("span", {
    className: "sidebar-nav__item-icon material-symbols-outlined"
  }, item.icon), React.createElement("span", {
    className: "sidebar-nav__item-copy"
  }, React.createElement("strong", null, item.label), React.createElement("span", null, item.description))))), React.createElement("div", {
    className: "sidebar-footer"
  }, React.createElement("button", {
    type: "button",
    className: "sidebar-foot-button",
    onClick: () => navigate("/")
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "restart_alt"), React.createElement("span", {
    className: "sidebar-foot-button__label"
  }, "\u56DE\u5230\u9996\u9875")), React.createElement("button", {
    type: "button",
    className: "sidebar-foot-button",
    onClick: onOpenDisclaimer
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "balance"), React.createElement("span", {
    className: "sidebar-foot-button__label"
  }, "\u514D\u8D23\u58F0\u660E")), React.createElement("div", {
    className: "sidebar-link-row"
  }, React.createElement("button", {
    type: "button",
    className: "sidebar-foot-chip sidebar-foot-chip--single",
    onClick: () => openExternalUrl(githubUrl)
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "code"), React.createElement("span", null, APP_VISIBLE_COPY.githubLabel))), React.createElement("div", {
    className: "sidebar-brand"
  }, React.createElement("span", {
    className: "sidebar-brand__dot"
  }), React.createElement("div", null, React.createElement("p", {
    className: "sidebar-brand__name"
  }, APP_VISIBLE_COPY.footerVersion), React.createElement("p", {
    className: "sidebar-brand__sub"
  }, APP_VISIBLE_COPY.footerStamp)))));
}
function SettingsPage({
  onOpenDisclaimer
}) {
  const navigate = useNavigate();
  const [settings, setSettings] = useState(DEFAULT_SETTINGS);
  const [runSettings, setRunSettings] = useState(DEFAULT_RUN_SETTINGS);
  const [runContext, setRunContext] = useState({
    controlled_run: false,
    explicit_run_context: false
  });
  const [showAuthCode, setShowAuthCode] = useState(false);
  const [showApiKey, setShowApiKey] = useState(false);
  const [pageError, setPageError] = useState("");
  const [emailStatus, setEmailStatus] = useState({
    status: "idle",
    message: ""
  });
  const [apiStatus, setApiStatus] = useState({
    status: "idle",
    message: ""
  });
  const [bootstrapState, setBootstrapState] = useState("bootstrapping");
  const [bootstrapError, setBootstrapError] = useState("");
  const [initialSetup, setInitialSetup] = useState(false);
  const [starting, setStarting] = useState(false);
  const saveTimerRef = useRef(null);
  const autostartTimerRef = useRef(null);
  const autostartTriggeredRef = useRef(false);
  useEffect(() => {
    let active = true;
    (async () => {
      try {
        const state = await loadShellState();
        if (!active) return;
        setSettings(state.settings);
        setRunSettings(state.runSettings);
        setRunContext(state.runContext);
        setInitialSetup(state.needsInitialSetup && !hasExplicitQaRunContext(state.runContext));
        setBootstrapError("");
        setBootstrapState("bootstrapped");
      } catch (error) {
        if (active) {
          setBootstrapError(error.message || "初始化设置失败，请重启应用。");
          setBootstrapState("bootstrap_failed");
        }
      }
    })();
    return () => {
      active = false;
      if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
      if (autostartTimerRef.current) clearTimeout(autostartTimerRef.current);
    };
  }, []);
  useEffect(() => {
    if (bootstrapState !== "bootstrapped") return undefined;
    if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
    saveTimerRef.current = setTimeout(() => {
      persistUserSettings(settings, runSettings, runContext).catch(error => {
        console.error("Failed to save settings", error);
      });
    }, 400);
    return () => {
      if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
    };
  }, [bootstrapState, settings, runSettings, runContext]);
  const controlledRun = hasExplicitQaRunContext(runContext);
  const emailParts = splitEmailAddress(settings.email);
  const dateError = validateDateRange(runSettings.date_from, runSettings.date_to);
  const canStart = !validateEmail(settings.email) && !!String(settings.company || "").trim() && !!String(settings.save_path || "").trim() && !dateError;
  useEffect(() => {
    if (bootstrapState !== "bootstrapped" || starting || autostartTriggeredRef.current || !shouldAutostartControlledRun(runContext)) return undefined;
    if (isControlledAutostartConsumed(runContext)) {
      autostartTriggeredRef.current = true;
      return undefined;
    }
    const delayMs = Math.max(0, Number(runContext.autostart_delay_ms || 0));
    autostartTimerRef.current = setTimeout(() => {
      autostartTriggeredRef.current = true;
      markControlledAutostartConsumed(runContext);
      handleStart();
    }, delayMs);
    return () => {
      if (autostartTimerRef.current) clearTimeout(autostartTimerRef.current);
    };
  }, [bootstrapState, runContext, starting, settings, runSettings]);
  function updateSetting(key, value) {
    setSettings(current => ({
      ...current,
      [key]: value
    }));
    setPageError("");
    if (key === "email" || key === "auth_code") setEmailStatus({
      status: "idle",
      message: ""
    });
    if (key === "email" || key === "api_key") setApiStatus({
      status: "idle",
      message: ""
    });
  }
  function setDateValue(field, value) {
    setRunSettings(current => ({
      ...current,
      [field]: value,
      quick_range: "custom"
    }));
    setPageError("");
  }
  async function handleChooseDirectory() {
    if (controlledRun) return;
    try {
      const result = await SettingsRpc.chooseDirectory(window.RpcClient);
      if (result && !result.cancelled && result.path) updateSetting("save_path", result.path);
    } catch (error) {
      setPageError(error.message || "选择目录失败。");
    }
  }
  async function saveSecretInput(name, settingKey) {
    const value = String(settings[settingKey] || "");
    if (!value) return;
    await SettingsRpc.setSecret(window.RpcClient, name, value, settings.remember_settings === false ? "session" : "persistent");
    setSettings(current => ({
      ...current,
      [settingKey]: ""
    }));
  }
  async function handleTestEmailAuth() {
    const emailError = validateEmail(settings.email);
    if (emailError) {
      setEmailStatus({
        status: "error",
        message: emailError
      });
      return;
    }
    setEmailStatus({
      status: "testing",
      message: "正在测试邮箱授权码..."
    });
    try {
      await persistUserSettings(settings, runSettings, runContext);
      if (settings.auth_code) {
        await saveSecretInput("mail.imap.auth-code", "auth_code");
      }
      const account = window.invoiceFlowMailboxAccount;
      if (!account) throw new Error("请先填写有效邮箱地址。");
      const result = await SettingsRpc.testAccount(window.RpcClient, account.accountId);
      setEmailStatus({
        status: result && result.success ? "success" : "error",
        message: result && result.message ? result.message : "邮箱授权验证失败。"
      });
    } catch (error) {
      setEmailStatus({
        status: "error",
        message: error.message || "邮箱授权验证失败。"
      });
    }
  }
  async function handleTestConnection() {
    setApiStatus({
      status: "testing",
      message: "正在测试 API Key..."
    });
    try {
      if (settings.api_key) await saveSecretInput("deepseek.api-key", "api_key");
      const result = await SettingsRpc.testProvider(window.RpcClient, "deepseek", "deepseek.api-key");
      setApiStatus({
        status: result && result.success ? "success" : "error",
        message: result && result.message ? result.message : "API Key 测试失败。"
      });
    } catch (error) {
      setApiStatus({
        status: "error",
        message: error.message || "API Key 测试失败。"
      });
    }
  }
  async function handleStart() {
    const emailError = validateEmail(settings.email);
    if (emailError) return setPageError(emailError);
    if (!settings.company || !settings.company.trim()) return setPageError("请填写公司名称。");
    if (!settings.save_path) return setPageError("请选择输出目录。");
    if (dateError) return setPageError(dateError);
    setStarting(true);
    try {
      if (settings.auth_code) await saveSecretInput("mail.imap.auth-code", "auth_code");
      if (settings.api_key) await saveSecretInput("deepseek.api-key", "api_key");
      await persistUserSettings(settings, runSettings, runContext);
      const accountId = window.invoiceFlowMailboxAccount?.accountId || window.invoiceFlowSettingsSnapshot?.currentAccountId || runContext.account_id;
      if (!accountId) {
        setPageError("请先配置邮箱账户。");
        return;
      }
      const runId = window.crypto?.randomUUID ? window.crypto.randomUUID() : `run-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
      const result = await RunPageRpc.startRun(window.RpcClient, {
        runId,
        accountId,
        dateFrom: runSettings.date_from,
        dateTo: runSettings.date_to,
        outputDirectory: settings.save_path,
        companyName: settings.company.trim(),
        runMode: "full"
      });
      if (!result || !result.accepted) {
        const messages = {
          RUN_ALREADY_ACTIVE: "已有任务正在运行。",
          RUN_CONFIGURATION_SNAPSHOT_MISSING: "邮箱账户配置已变化，请重新加载设置。",
          RPC_INVALID_PARAMS: "任务参数无效，请检查设置。"
        };
        setPageError(messages[result && result.rejectionCode] || "任务启动失败。");
        return;
      }
      writeSessionValue(SESSION_ACTIVE_RUN_KEY, {
        runId
      });
      navigate("/processing");
    } catch (error) {
      setPageError(error.message || "任务启动失败。");
    } finally {
      setStarting(false);
    }
  }
  if (bootstrapState === "bootstrapping") {
    return React.createElement(BootstrapStatePage, {
      active: "settings",
      onOpenDisclaimer: onOpenDisclaimer,
      eyebrow: UI_COPY.pages.settings.eyebrow,
      title: UI_COPY.pages.settings.bootstrapTitle,
      description: UI_COPY.pages.settings.bootstrapDescription,
      tone: "info",
      message: UI_COPY.pages.settings.bootstrapMessage,
      footerText: UI_COPY.pages.settings.footerText
    });
  }
  if (bootstrapState === "bootstrap_failed") {
    return React.createElement(BootstrapStatePage, {
      active: "settings",
      onOpenDisclaimer: onOpenDisclaimer,
      eyebrow: UI_COPY.pages.settings.eyebrow,
      title: UI_COPY.pages.settings.bootstrapTitle,
      description: UI_COPY.pages.settings.errorDescription,
      tone: "error",
      message: bootstrapError || "初始化设置失败，请重启应用。",
      footerText: UI_COPY.pages.settings.footerText
    });
  }
  return React.createElement(AppShell, {
    active: "settings",
    onOpenDisclaimer: onOpenDisclaimer,
    initialSetup: initialSetup,
    footerLeft: React.createElement("label", {
      className: "toggle-row"
    }, React.createElement("input", {
      type: "checkbox",
      checked: settings.remember_settings !== false,
      onChange: event => updateSetting("remember_settings", event.target.checked)
    }), React.createElement("span", null, "\u81EA\u52A8\u8BB0\u4F4F\u5F53\u524D\u914D\u7F6E")),
    footerRight: React.createElement("button", {
      type: "button",
      className: "btn btn--primary",
      onClick: handleStart,
      disabled: !canStart || starting
    }, React.createElement("span", {
      className: "material-symbols-outlined"
    }, starting ? "sync" : "play_arrow"), React.createElement("span", null, starting ? "启动中..." : "开始提取"))
  }, React.createElement("div", {
    className: "page-wrap page-wrap--settings"
  }, React.createElement(PageHeader, {
    eyebrow: UI_COPY.pages.settings.eyebrow,
    title: UI_COPY.pages.settings.title,
    badge: controlledRun ? React.createElement(StatusPill, {
      tone: "info",
      icon: "lock_clock"
    }, "\u53D7\u63A7\u524D\u7AEF\u590D\u8DD1") : null
  }), pageError ? React.createElement(NoticeBox, {
    tone: "error"
  }, pageError) : null, controlledRun ? React.createElement("p", {
    className: "page-inline-note"
  }, UI_COPY.pages.settings.controlledNotice) : null, React.createElement("div", {
    className: "settings-grid settings-grid--fit"
  }, React.createElement("div", {
    className: "settings-column"
  }, React.createElement("section", {
    className: "surface-card"
  }, React.createElement(SectionHeader, {
    icon: "mail",
    title: "\u90AE\u7BB1\u6E90\u914D\u7F6E",
    indicator: React.createElement(StatusPill, {
      tone: "warning",
      icon: "mail"
    }, "\u5F85\u786E\u8BA4")
  }), React.createElement("div", {
    className: "card-stack card-stack--compact"
  }, React.createElement("div", {
    className: "field-block"
  }, React.createElement("label", {
    className: "field-label"
  }, "\u90AE\u7BB1\u5730\u5740"), React.createElement("div", {
    className: "field-row"
  }, React.createElement("div", {
    className: "field-shell",
    style: {
      flex: 1
    }
  }, React.createElement("span", {
    className: "field-icon material-symbols-outlined"
  }, "alternate_email"), React.createElement("input", {
    type: "text",
    className: "field-input",
    placeholder: "\u90AE\u7BB1\u7528\u6237\u540D",
    value: emailParts.username,
    onChange: event => {
      const username = event.target.value.replace(/@/g, "");
      updateSetting("email", username ? `${username}@${emailParts.domain}` : "");
    }
  })), React.createElement("span", {
    className: "u-text-muted"
  }, "@"), React.createElement("div", {
    style: {
      width: 136
    }
  }, React.createElement(DarkSelect, {
    value: emailParts.domain,
    options: EMAIL_DOMAIN_OPTIONS,
    ariaLabel: "\u90AE\u7BB1\u57DF\u540D",
    onChange: domain => updateSetting("email", emailParts.username ? `${emailParts.username}@${domain}` : "")
  }))), React.createElement("p", {
    className: "field-help"
  }, "\u5F53\u524D\u652F\u6301 QQ \u90AE\u7BB1\u4E0E 163 \u90AE\u7BB1\uFF0C\u57DF\u540D\u4F1A\u51B3\u5B9A IMAP \u901A\u9053\u3002")), React.createElement("div", {
    className: "field-block"
  }, React.createElement("label", {
    className: "field-label"
  }, "\u6388\u6743\u7801"), React.createElement("div", {
    className: "field-shell"
  }, React.createElement("span", {
    className: "field-icon material-symbols-outlined"
  }, "key"), React.createElement("input", {
    type: showAuthCode ? "text" : "password",
    className: "field-input u-mono",
    placeholder: "\u8BF7\u8F93\u5165\u90AE\u7BB1\u6388\u6743\u7801",
    value: settings.auth_code,
    onChange: event => updateSetting("auth_code", event.target.value)
  }), React.createElement("button", {
    type: "button",
    className: "field-mask-toggle",
    onClick: () => setShowAuthCode(current => !current)
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, showAuthCode ? "visibility" : "visibility_off"))), React.createElement("div", {
    className: "footer-cluster"
  }, React.createElement("button", {
    type: "button",
    className: "btn btn--secondary btn--sm",
    onClick: handleTestEmailAuth,
    disabled: emailStatus.status === "testing"
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, emailStatus.status === "testing" ? "sync" : "verified"), React.createElement("span", null, emailStatus.status === "testing" ? "测试中..." : "测试邮箱授权码"))), emailStatus.status !== "idle" ? React.createElement(InlineStatus, {
    tone: toneFromAsyncStatus(emailStatus.status)
  }, emailStatus.message) : null))), React.createElement("section", {
    className: "surface-card"
  }, React.createElement(SectionHeader, {
    icon: "folder_open",
    title: "\u8F93\u51FA\u76EE\u5F55\u8BBE\u7F6E",
    indicator: controlledRun ? React.createElement(StatusPill, {
      tone: "info",
      icon: "lock"
    }, "\u5DF2\u9501\u5B9A") : "保存原件与结果"
  }), React.createElement("div", {
    className: "card-stack card-stack--compact"
  }, React.createElement("div", {
    className: joinClasses("field-shell", "field-shell--readonly", !settings.save_path && "field-shell--error")
  }, React.createElement("span", {
    className: "field-icon material-symbols-outlined"
  }, "folder"), React.createElement("input", {
    type: "text",
    readOnly: true,
    className: "field-input u-mono",
    value: settings.save_path,
    placeholder: "\u8BF7\u9009\u62E9\u672C\u5730\u8F93\u51FA\u76EE\u5F55",
    title: settings.save_path
  })), React.createElement("div", {
    className: "footer-cluster"
  }, React.createElement("button", {
    type: "button",
    className: "btn btn--secondary btn--sm",
    onClick: handleChooseDirectory,
    disabled: controlledRun
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "folder_open"), React.createElement("span", null, controlledRun ? "路径已锁定" : "浏览目录"))), React.createElement("p", {
    className: "field-help"
  }, controlledRun ? "当前为受控前端复跑，本轮输出目录由诊断上下文锁定。" : "系统会在此目录下继续按发票类型落盘，并生成待人工复核目录。")))), React.createElement("div", {
    className: "settings-column"
  }, React.createElement("section", {
    className: "surface-card"
  }, React.createElement(SectionHeader, {
    icon: "psychology",
    title: "\u667A\u80FD\u5904\u7406\u5F15\u64CE",
    indicator: React.createElement(StatusPill, {
      tone: "success",
      icon: "auto_awesome"
    }, "DeepSeek")
  }), React.createElement("div", {
    className: "card-stack card-stack--compact"
  }, React.createElement("div", {
    className: "field-block"
  }, React.createElement("div", {
    style: {
      display: "flex",
      justifyContent: "space-between",
      alignItems: "center",
      gap: 12,
      flexWrap: "wrap"
    }
  }, React.createElement("label", {
    className: "field-label"
  }, "DeepSeek API Key"), React.createElement("a", {
    className: "field-inline-action",
    href: DEEPSEEK_API_KEYS_URL,
    target: "_blank",
    rel: "noreferrer"
  }, React.createElement("span", {
    className: "material-symbols-outlined",
    style: {
      fontSize: 14
    }
  }, "open_in_new"), React.createElement("span", null, "\u83B7\u53D6 API Key"))), React.createElement("div", {
    className: "field-row"
  }, React.createElement("div", {
    className: "field-shell",
    style: {
      flex: 1
    }
  }, React.createElement("span", {
    className: "field-icon material-symbols-outlined"
  }, "vpn_key"), React.createElement("input", {
    type: showApiKey ? "text" : "password",
    className: "field-input u-mono",
    placeholder: "sk-...",
    value: settings.api_key,
    onChange: event => updateSetting("api_key", event.target.value)
  }), React.createElement("button", {
    type: "button",
    className: "field-mask-toggle",
    onClick: () => setShowApiKey(current => !current)
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, showApiKey ? "visibility" : "visibility_off"))), React.createElement("button", {
    type: "button",
    className: "btn btn--secondary btn--sm",
    onClick: handleTestConnection,
    disabled: apiStatus.status === "testing"
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, apiStatus.status === "testing" ? "sync" : "link"), React.createElement("span", null, apiStatus.status === "testing" ? "测试中..." : "测试 API Key"))), apiStatus.status === "idle" ? React.createElement("p", {
    className: "field-help"
  }, "\u989D\u5EA6\u4E0D\u8DB3\u65F6\u4F1A\u76F4\u63A5\u63D0\u793A\uFF0C\u540E\u7EED\u5904\u7406\u9875\u4E5F\u4F1A\u540C\u6B65\u663E\u793A quota \u76F8\u5173\u8B66\u544A\u3002") : null, apiStatus.status !== "idle" ? React.createElement(InlineStatus, {
    tone: toneFromAsyncStatus(apiStatus.status)
  }, apiStatus.message) : null))), React.createElement("section", {
    className: "surface-card surface-card--compact settings-date-card"
  }, React.createElement(SectionHeader, {
    icon: "calendar_month",
    title: "\u63D0\u53D6\u65F6\u95F4\u8303\u56F4",
    indicator: controlledRun ? React.createElement(StatusPill, {
      tone: "info",
      icon: "lock"
    }, "\u5DF2\u9501\u5B9A") : null
  }), React.createElement("div", {
    className: "card-stack card-stack--compact"
  }, React.createElement("div", {
    className: "field-row field-row--dates settings-date-row"
  }, React.createElement(DateField, {
    label: "\u5F00\u59CB\u65E5\u671F",
    value: runSettings.date_from,
    onChange: value => setDateValue("date_from", value),
    disabled: controlledRun
  }), React.createElement("span", {
    className: "field-separator"
  }, "\u2014"), React.createElement(DateField, {
    label: "\u7ED3\u675F\u65E5\u671F",
    value: runSettings.date_to,
    onChange: value => setDateValue("date_to", value),
    disabled: controlledRun
  })), controlledRun ? React.createElement("p", {
    className: "field-help field-help--subtle"
  }, "\u5F53\u524D\u4E3A\u53D7\u63A7\u590D\u8DD1\uFF0C\u65E5\u671F\u8303\u56F4\u5DF2\u6309\u4E0A\u4E0B\u6587\u9501\u5B9A\u3002") : React.createElement("p", {
    className: "field-help field-help--subtle"
  }, "\u786E\u8BA4\u8D77\u6B62\u65E5\u671F\u540E\u53EF\u76F4\u63A5\u5F00\u59CB\u63D0\u53D6\uFF0C\u672C\u9875\u4E0D\u518D\u8FDB\u5165\u72EC\u7ACB\u786E\u8BA4\u6D41\u7A0B\u3002"))), React.createElement("section", {
    className: "surface-card"
  }, React.createElement(SectionHeader, {
    icon: "business",
    title: "\u516C\u53F8\u914D\u7F6E",
    indicator: React.createElement(StatusPill, {
      tone: settings.company && settings.company.trim() ? "success" : "warning",
      icon: "apartment"
    }, "\u8D2D\u4E70\u65B9\u6821\u9A8C")
  }), React.createElement("div", {
    className: "card-stack card-stack--compact"
  }, React.createElement("div", {
    className: "field-block"
  }, React.createElement("label", {
    className: "field-label"
  }, "\u516C\u53F8 ", React.createElement("span", {
    className: "field-required"
  }, "*")), React.createElement("div", {
    className: joinClasses("field-shell", !settings.company || !settings.company.trim() ? "field-shell--error" : "")
  }, React.createElement("span", {
    className: "field-icon material-symbols-outlined"
  }, "domain"), React.createElement("input", {
    type: "text",
    className: "field-input",
    placeholder: "\u586B\u5199\u7528\u4E8E\u5339\u914D\u8D2D\u4E70\u65B9\u7684\u516C\u53F8\u540D\u79F0",
    value: settings.company || "",
    onChange: event => updateSetting("company", event.target.value)
  })), !settings.company || !settings.company.trim() ? React.createElement("p", {
    className: "field-help field-help--subtle"
  }, "\u586B\u5199\u516C\u53F8\u540D\u79F0\u540E\u6309\u8D2D\u4E70\u65B9\u5B57\u6BB5\u5339\u914D\u3002") : React.createElement("p", {
    className: "field-help field-help--subtle"
  }, "\u6309\u8D2D\u4E70\u65B9\u5305\u542B\u201C", settings.company.trim(), "\u201D\u8FDB\u884C\u5339\u914D\uFF1B\u660E\u786E\u4E0D\u5339\u914D\u7684\u7968\u636E\u4F1A\u5355\u72EC\u8FDB\u5165\u201C\u975E\u76EE\u6807\u516C\u53F8\u53D1\u7968\u201D\u3002"))))))));
}
function ProcessingPage({
  onOpenDisclaimer
}) {
  const navigate = useNavigate();
  const [progressState, setProgressState] = useState(DEFAULT_PROGRESS);
  const redirectRef = useRef(false);
  const terminalBodyRef = useRef(null);
  const activeRunId = readSessionValue(SESSION_ACTIVE_RUN_KEY).runId || "";
  useEffect(() => {
    let active = true;
    let timer = null;
    const applyProgress = data => {
      if (!active || !data) return;
      setProgressState({
        ...DEFAULT_PROGRESS,
        ...data,
        stats: Object.assign({}, DEFAULT_PROGRESS.stats, data.stats || {})
      });
      if (!redirectRef.current && ["completed", "failed", "cancelled"].includes(data.run_state) && !data.is_running) {
        redirectRef.current = true;
        setTimeout(() => navigate("/analysis"), 1200);
      }
    };
    if (!activeRunId) {
      setProgressState(current => ({
        ...current,
        last_error: "任务记录不可用，请返回重新开始。"
      }));
      return () => {
        active = false;
      };
    }
    const feed = RunPageRpc.watchProgress(window.RpcClient, activeRunId, applyProgress);
    feed.initial.catch(error => {
      if (active) setProgressState(current => ({
        ...current,
        last_error: error.message || "获取进度失败。"
      }));
    });
    timer = setInterval(() => {
      feed.refresh().catch(error => {
        if (active) setProgressState(current => ({
          ...current,
          last_error: error.message || "获取进度失败。"
        }));
      });
    }, 1000);
    return () => {
      active = false;
      if (timer) clearInterval(timer);
      feed.dispose();
    };
  }, [navigate, activeRunId]);
  async function handleStop() {
    if (!progressState.can_stop) return;
    try {
      const result = await RunPageRpc.stopRun(window.RpcClient, activeRunId);
      if (!result || !result.accepted) {
        window.alert(result && result.errorCode === "RUN_NOT_CANCELLABLE" ? "当前任务已无法停止。" : "停止指令发送失败。");
      }
    } catch (error) {
      window.alert(error.message || "停止指令发送失败。");
    }
  }
  const stats = progressState.stats || DEFAULT_PROGRESS.stats;
  const logs = progressState.logs || [];
  const statusTone = progressState.run_state === "failed" ? "error" : progressState.run_state === "completed" ? "success" : progressState.is_running ? "info" : "neutral";
  const statusLabel = progressState.run_state === "failed" ? "处理失败" : progressState.run_state === "completed" ? "处理完成" : progressState.is_running ? "实时连接已建立" : UI_COPY.pages.processing.statusWaiting;
  useEffect(() => {
    if (!terminalBodyRef.current) return;
    terminalBodyRef.current.scrollTop = terminalBodyRef.current.scrollHeight;
  }, [logs, progressState.is_running, progressState.stop_requested]);
  return React.createElement(AppShell, {
    active: "processing",
    onOpenDisclaimer: onOpenDisclaimer,
    contentScrollable: false,
    footerLeft: React.createElement("p", {
      className: "footer-meta"
    }, UI_COPY.pages.processing.closeHint),
    footerRight: React.createElement(StatusPill, {
      tone: statusTone,
      icon: progressState.is_running ? "radar" : "schedule"
    }, statusLabel)
  }, React.createElement("div", {
    className: "page-wrap page-wrap--processing"
  }, React.createElement(PageHeader, {
    eyebrow: UI_COPY.pages.processing.eyebrow,
    title: UI_COPY.pages.processing.title
  }), React.createElement("section", {
    className: "surface-card surface-card--hero"
  }, React.createElement("div", {
    className: "progress-hero"
  }, React.createElement("div", {
    className: "progress-main"
  }, React.createElement("div", null, React.createElement("p", {
    className: "progress-kicker"
  }, UI_COPY.pages.processing.currentOperation), React.createElement("h2", {
    className: "progress-title"
  }, progressState.status_text), React.createElement("p", {
    className: "progress-meta"
  }, progressState.stop_requested ? UI_COPY.pages.processing.stopDetail : UI_COPY.pages.processing.processingDetail)), React.createElement("div", {
    className: "progress-actions"
  }, React.createElement("div", {
    className: "progress-value"
  }, React.createElement("strong", {
    className: "u-tabular"
  }, progressState.progress, "%"), React.createElement("span", null, UI_COPY.pages.processing.progressLabel)), React.createElement("button", {
    type: "button",
    className: "btn btn--danger",
    onClick: handleStop,
    disabled: !progressState.can_stop
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, progressState.stop_requested ? "sync" : "close"), React.createElement("span", null, progressState.stop_requested ? "正在停止..." : "停止运行")))), React.createElement("div", {
    className: "progress-track"
  }, React.createElement("div", {
    className: "progress-fill",
    style: {
      width: `${progressState.progress}%`
    }
  })), React.createElement("div", {
    className: "progress-caption"
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "sync"), React.createElement("span", null, progressState.stop_requested ? UI_COPY.pages.processing.stopPending : UI_COPY.pages.processing.liveRefresh)), progressState.last_error && progressState.run_state === "failed" ? React.createElement(NoticeBox, {
    tone: "error"
  }, progressState.last_error) : null, progressState.quota_exhausted && progressState.quota_message ? React.createElement(NoticeBox, {
    tone: "warning"
  }, progressState.quota_message) : null, progressState.stop_requested && progressState.run_state !== "failed" ? React.createElement(NoticeBox, {
    tone: "warning"
  }, UI_COPY.pages.processing.stopNotice) : null)), React.createElement("div", {
    className: "metrics-grid"
  }, React.createElement("div", {
    className: "metric-card metric-card--blue"
  }, React.createElement("div", {
    className: "metric-card__icon"
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "mail")), React.createElement("div", {
    className: "metric-card__copy"
  }, React.createElement("p", {
    className: "metric-card__label"
  }, "\u5DF2\u626B\u63CF\u90AE\u4EF6"), React.createElement("p", {
    className: "metric-card__value u-tabular"
  }, stats.emails || 0))), React.createElement("div", {
    className: "metric-card metric-card--green"
  }, React.createElement("div", {
    className: "metric-card__icon"
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "description")), React.createElement("div", {
    className: "metric-card__copy"
  }, React.createElement("p", {
    className: "metric-card__label"
  }, "\u5DF2\u8BC6\u522B\u53D1\u7968"), React.createElement("p", {
    className: "metric-card__value u-tabular"
  }, stats.invoices || 0))), React.createElement("div", {
    className: "metric-card metric-card--amber"
  }, React.createElement("div", {
    className: "metric-card__icon"
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "warning")), React.createElement("div", {
    className: "metric-card__copy"
  }, React.createElement("p", {
    className: "metric-card__label"
  }, "\u5F02\u5E38\u5904\u7406"), React.createElement("p", {
    className: "metric-card__value u-tabular"
  }, stats.errors || 0)))), React.createElement("section", {
    className: "surface-card surface-card--terminal processing-terminal",
    style: {
      flex: 1,
      minHeight: 0,
      display: "flex",
      flexDirection: "column"
    }
  }, React.createElement("div", {
    className: "terminal-head"
  }, React.createElement("div", {
    className: "terminal-title"
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, "terminal"), React.createElement("span", null, "\u5B9E\u65F6\u6267\u884C\u65E5\u5FD7")), React.createElement("div", {
    className: "terminal-dots"
  }, React.createElement("span", {
    className: "terminal-dot"
  }), React.createElement("span", {
    className: "terminal-dot"
  }), React.createElement("span", {
    className: "terminal-dot"
  }))), React.createElement("div", {
    ref: terminalBodyRef,
    className: "terminal-body"
  }, React.createElement("p", {
    className: "terminal-intro"
  }, "-- \u53D1\u7968\u52A9\u624B\u5F15\u64CE\u5DF2\u8FDE\u63A5 --", React.createElement("br", null), "-- \u8FDB\u5EA6\u4E0E\u65E5\u5FD7\u5B9E\u65F6\u5237\u65B0\u4E2D --"), logs.map((log, index) => React.createElement("div", {
    key: `${log.time || "log"}-${index}`,
    className: "terminal-line"
  }, React.createElement("span", {
    className: "terminal-time"
  }, safeText(log.time, "[实时]")), React.createElement("span", {
    className: resolveLogToneClass(log)
  }, safeText(log.type, "LOG")), React.createElement("span", {
    className: "terminal-text"
  }, safeText(log.msg, "-")))), progressState.is_running ? React.createElement("div", {
    className: "terminal-line",
    style: {
      marginTop: 4
    }
  }, React.createElement("span", {
    className: "terminal-time"
  }, "[\u5B9E\u65F6]"), React.createElement("span", {
    className: "terminal-kind terminal-kind--info"
  }, "\u72B6\u6001"), React.createElement("span", {
    className: "terminal-text"
  }, progressState.stop_requested ? "正在安全停止..." : "任务持续执行中...", React.createElement("span", {
    className: "terminal-cursor"
  }))) : null))));
}
function fileExtension(path) {
  const value = String(path || "").toLowerCase();
  if (!value) return "";
  if (value.endsWith(".url.txt")) return ".url.txt";
  const match = value.match(/(\.[^./\\]+)$/);
  return match ? match[1] : "";
}
function describeResultFile(path) {
  const extension = fileExtension(path);
  if ([".pdf", ".jpg", ".jpeg", ".png", ".ofd", ".xml"].includes(extension)) {
    return {
      extension,
      fileKindLabel: extension === ".xml" ? "XML 原件" : extension === ".ofd" ? "OFD 原件" : [".jpg", ".jpeg", ".png"].includes(extension) ? "图片原件" : "PDF 原件"
    };
  }
  if ([".txt", ".url.txt", ".log", ".json"].includes(extension)) {
    return {
      extension,
      fileKindLabel: extension === ".url.txt" ? "链接记录" : "记录文件"
    };
  }
  return {
    extension,
    fileKindLabel: "文件"
  };
}
function normalizeSuccessInvoices(items) {
  return (items || []).map((item, index) => {
    const path = item.path || "";
    const fileMeta = describeResultFile(path);
    return {
      key: path || `${item.date || "row"}-${index}`,
      date: safeText(item.date),
      amount: safeText(item.amount),
      merchant: safeText(item.merchant || item.vendor),
      archiveType: String(item.category || "").trim() || safeText(fileMeta.fileKindLabel),
      path,
      fileName: fileNameFromPath(path)
    };
  });
}
function normalizeGroupedErrors(groups) {
  return (groups || []).map((group, groupIndex) => ({
    key: group.key || `group-${groupIndex}`,
    label: group.key === "retained_record" ? "暂存记录" : group.key === "manual_review" ? "待人工复核" : group.key === "processing_error" ? "真实异常" : group.label || "待处理记录",
    count: Number(group.count || 0),
    items: (group.items || []).map((item, itemIndex) => {
      const path = item.path || "";
      return {
        rowKey: path || `${group.key || "group"}-${itemIndex}`,
        date: safeText(item.date),
        reason: safeText(item.reason, group.label || "待处理"),
        status: safeText(item.status, "待处理"),
        merchant: safeText(item.merchant),
        path,
        fileName: fileNameFromPath(path)
      };
    })
  }));
}
function buildResultStatusText(successCount, manualCheckCount, retentionCount, processingErrorCount) {
  const pendingCount = manualCheckCount + retentionCount + processingErrorCount;
  if (successCount > 0 && pendingCount === 0) return "主要结果已经整理完成，本轮没有需要额外关注的记录。";
  if (successCount > 0 && pendingCount > 0) {
    if (manualCheckCount > 0 && retentionCount > 0 && processingErrorCount > 0) {
      return `已整理 ${successCount} 条成功记录；请先处理 ${manualCheckCount} 条待人工复核记录，另有 ${retentionCount} 条暂存记录与 ${processingErrorCount} 条真实异常需要查看。`;
    }
    if (manualCheckCount > 0 && retentionCount > 0) {
      return `已整理 ${successCount} 条成功记录；请优先查看 ${manualCheckCount} 条待人工复核记录，另有 ${retentionCount} 条暂存记录可按需导出查看。`;
    }
    if (manualCheckCount > 0 && processingErrorCount > 0) {
      return `已整理 ${successCount} 条成功记录；当前需要关注 ${manualCheckCount} 条待人工复核记录和 ${processingErrorCount} 条真实异常。`;
    }
    if (manualCheckCount > 0) {
      return `已整理 ${successCount} 条成功记录；当前需要你关注的是 ${manualCheckCount} 条待人工复核记录。`;
    }
    if (retentionCount > 0 && processingErrorCount > 0) {
      return `已整理 ${successCount} 条成功记录，另有 ${retentionCount} 条暂存记录和 ${processingErrorCount} 条真实异常需要查看。`;
    }
    if (retentionCount > 0) return `已整理 ${successCount} 条成功记录，另有 ${retentionCount} 条暂存记录可按需导出查看。`;
    if (processingErrorCount > 0) return `已整理 ${successCount} 条成功记录，但仍有 ${processingErrorCount} 条真实异常需要排查。`;
  }
  if (successCount === 0 && pendingCount > 0) {
    if (manualCheckCount > 0) return `本轮暂无可直接归档的记录；请先查看 ${manualCheckCount} 条待人工复核记录，其余结果可按需导出查看。`;
    if (retentionCount > 0 && processingErrorCount > 0) return `本轮暂无可直接归档的记录，当前有 ${retentionCount} 条暂存记录和 ${processingErrorCount} 条真实异常。`;
    if (retentionCount > 0) return "本轮暂无可直接归档的记录，当前结果可通过导出明细进一步查看。";
    if (processingErrorCount > 0) return `本轮暂无可直接归档的记录，当前有 ${processingErrorCount} 条真实异常需要排查。`;
  }
  return "本轮尚未生成可展示的结果记录。";
}
function ResultSummaryCard({
  icon,
  label,
  value,
  helper,
  tone = "info"
}) {
  return React.createElement("section", {
    className: joinClasses("stat-card", `stat-card--${tone}`)
  }, React.createElement("div", {
    className: "stat-card__top"
  }, React.createElement("div", {
    className: "stat-card__icon"
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, icon)), React.createElement("div", {
    className: "stat-card__copy"
  }, React.createElement("p", {
    className: "stat-card__label"
  }, label), React.createElement("p", {
    className: "stat-card__value u-tabular"
  }, value))), React.createElement("p", {
    className: "stat-card__helper"
  }, helper));
}
function ResultToolbarButton({
  icon,
  label,
  onClick,
  disabled = false,
  primary = false
}) {
  return React.createElement("button", {
    type: "button",
    className: joinClasses("btn", primary ? "btn--primary" : "btn--secondary", "btn--sm"),
    onClick: onClick,
    disabled: disabled
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, icon), React.createElement("span", null, label));
}
function ResultActionBanner({
  tone = "warning",
  icon,
  eyebrow,
  title,
  text,
  buttonLabel,
  onClick,
  disabled = false,
  pathText = "",
  chips = null
}) {
  return React.createElement("section", {
    className: joinClasses("manual-banner", tone === "neutral" && "manual-banner--calm", tone === "neutral" && "manual-banner--output")
  }, React.createElement("div", {
    className: "manual-banner__lead"
  }, React.createElement("div", {
    className: "manual-banner__icon"
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, icon)), React.createElement("div", null, React.createElement("p", {
    className: "manual-banner__eyebrow"
  }, eyebrow), React.createElement("p", {
    className: "manual-banner__title"
  }, title), React.createElement("p", {
    className: "manual-banner__text"
  }, text), pathText ? React.createElement("p", {
    className: "manual-banner__path u-mono",
    title: pathText
  }, pathText) : null, chips)), React.createElement("button", {
    type: "button",
    className: "btn btn--secondary",
    onClick: onClick,
    disabled: disabled
  }, React.createElement("span", {
    className: "material-symbols-outlined"
  }, icon), React.createElement("span", null, buttonLabel)));
}
function AnalysisPage({
  onOpenDisclaimer
}) {
  const navigate = useNavigate();
  const [summary, setSummary] = useState({});
  const [successInvoices, setSuccessInvoices] = useState([]);
  const [groupedErrors, setGroupedErrors] = useState([]);
  const [manualCheckPath, setManualCheckPath] = useState("");
  const [outputPath, setOutputPath] = useState("");
  const [resultBreakdown, setResultBreakdown] = useState({});
  const [quotaMessage, setQuotaMessage] = useState("");
  const [quotaExhausted, setQuotaExhausted] = useState(false);
  const [lastExportPath, setLastExportPath] = useState("");
  const [loadingError, setLoadingError] = useState("");
  const [exporting, setExporting] = useState(false);
  const [lastExportCounts, setLastExportCounts] = useState(null);
  const activeRunId = readSessionValue(SESSION_ACTIVE_RUN_KEY).runId || "";
  useEffect(() => {
    let active = true;
    let timer = null;
    const loadResults = async () => {
      try {
        const results = await RunPageRpc.getResults(window.RpcClient, activeRunId || null);
        if (!active || !results) return;
        setSummary(results.summary || {});
        setSuccessInvoices(normalizeSuccessInvoices(results.successInvoices || []));
        setGroupedErrors(normalizeGroupedErrors(results.groupedErrorInvoices || []));
        setManualCheckPath(results.manual_check_path || "");
        setResultBreakdown(results.resultBreakdown || results.summary && results.summary.result_breakdown || {});
        setQuotaExhausted(!!results.quota_exhausted);
        setQuotaMessage(results.quota_message || "");
        setLastExportPath(results.last_export_path || "");
        const baseOutput = results.output_path || parentFolder(results.manual_check_path || "") || window.invoiceFlowSettingsSnapshot?.lastOutputDirectory || "";
        setOutputPath(baseOutput);
        setLoadingError("");
      } catch (error) {
        if (active) setLoadingError(error.message || "结果加载失败。");
      }
    };
    loadResults();
    timer = setInterval(loadResults, 3000);
    return () => {
      active = false;
      if (timer) clearInterval(timer);
    };
  }, [activeRunId]);
  const totalErrors = useMemo(() => groupedErrors.reduce((acc, group) => acc + (group.count || group.items.length || 0), 0), [groupedErrors]);
  const successCount = Number(summary.success_count || successInvoices.length);
  const manualReviewGroup = groupedErrors.find(group => group.key === "manual_review");
  const manualCheckCount = Number(summary.manual_check_count || resultBreakdown.manual_review || manualReviewGroup && manualReviewGroup.count || 0);
  const retentionCount = Number(summary.retention_count || resultBreakdown.retained_record || 0);
  const processingErrorCount = Number(summary.processing_error_count || resultBreakdown.processing_error || 0);
  const pendingCount = manualCheckCount + retentionCount + processingErrorCount || Number(summary.error_count || totalErrors || 0);
  const totalResultCount = successCount + pendingCount;
  const statusText = buildResultStatusText(successCount, manualCheckCount, retentionCount, processingErrorCount);
  const groupedVisible = groupedErrors.filter(group => Number(group.count || group.items.length || 0) > 0);
  async function handleOpenOutput() {
    try {
      const result = await RunPageRpc.openRunFolder(window.RpcClient, activeRunId || null);
      if (!result || !result.succeeded) window.alert(result && result.message || "输出目录无法打开。");
    } catch (error) {
      window.alert(error.message || "输出目录无法打开。");
    }
  }
  async function handleOpenManualCheck() {
    try {
      const result = await RunPageRpc.openManualReviewFolder(window.RpcClient, activeRunId || null);
      if (!result || !result.succeeded) window.alert(result && result.message || "人工复核目录无法打开。");
    } catch (error) {
      window.alert(error.message || "人工复核目录无法打开。");
    }
  }
  async function handleExport() {
    setExporting(true);
    try {
      const result = await RunPageRpc.exportReport(window.RpcClient, activeRunId);
      if (result && result.reportPath) {
        const exportedPath = result.reportPath;
        setLastExportPath(exportedPath);
        setLastExportCounts({
          invoices: result.invoiceRowCount,
          reviews: result.manualReviewRowCount
        });
        const opened = await RunPageRpc.openReport(window.RpcClient, result.runId, result.reportPath, result.contentHash);
        if (!opened || !opened.succeeded) {
          window.alert(opened && opened.message || "报表已导出，但打开文件失败。");
        }
      } else {
        window.alert("导出失败。");
      }
    } catch (error) {
      window.alert(error.message || "导出失败。");
    } finally {
      setExporting(false);
    }
  }
  const summaryCards = [{
    key: "success",
    icon: "task_alt",
    label: "处理成功",
    value: successCount,
    helper: successCount > 0 ? "归档文件已写入输出目录。" : "当前还没有成功记录。",
    tone: "success"
  }, {
    key: "retention",
    icon: "inventory_2",
    label: "暂存记录",
    value: retentionCount,
    helper: retentionCount > 0 ? "系统已保全但未纳入成功归档，可按需导出查看。" : "当前没有暂存记录。",
    tone: "info"
  }, {
    key: "manual",
    icon: "folder_open",
    label: "待人工复核",
    value: manualCheckCount,
    helper: manualCheckCount > 0 ? "这是当前需要优先处理的内容。" : "当前没有待人工复核项。",
    tone: manualCheckCount > 0 ? "warning" : "info"
  }];
  return React.createElement(AppShell, {
    active: "analysis",
    onOpenDisclaimer: onOpenDisclaimer,
    contentScrollable: false,
    footerLeft: React.createElement("button", {
      type: "button",
      className: "btn btn--ghost",
      onClick: () => navigate("/")
    }, React.createElement("span", {
      className: "material-symbols-outlined"
    }, "add_circle"), React.createElement("span", null, "\u5F00\u59CB\u65B0\u6279\u6B21")),
    footerRight: lastExportPath ? React.createElement("p", {
      className: "footer-meta",
      title: lastExportCounts ? `发票 ${lastExportCounts.invoices} 条，人工复核 ${lastExportCounts.reviews} 条` : ""
    }, "\u6700\u8FD1\u5BFC\u51FA: ", fileNameFromPath(lastExportPath)) : null
  }, React.createElement("div", {
    className: "page-wrap page-wrap--analysis"
  }, React.createElement(PageHeader, {
    eyebrow: UI_COPY.pages.analysis.eyebrow,
    title: UI_COPY.pages.analysis.title
  }), React.createElement("section", {
    className: "surface-card surface-card--hero"
  }, React.createElement("div", {
    style: {
      padding: 18,
      display: "flex",
      flexDirection: "column",
      gap: 16
    }
  }, React.createElement("div", {
    style: {
      display: "flex",
      justifyContent: "space-between",
      alignItems: "flex-start",
      gap: 16,
      flexWrap: "wrap"
    }
  }, React.createElement("div", null, React.createElement("p", {
    className: "progress-kicker"
  }, UI_COPY.pages.analysis.statusSummary), React.createElement("h2", {
    className: "progress-title"
  }, UI_COPY.pages.analysis.resultTitle), React.createElement("p", {
    className: "progress-meta"
  }, "\u5171\u6574\u7406 ", React.createElement("strong", {
    className: "u-tabular"
  }, totalResultCount), " \u6761\u7ED3\u679C\u8BB0\u5F55\u3002", statusText)), React.createElement("div", {
    className: "footer-cluster"
  }, React.createElement(ResultToolbarButton, {
    icon: exporting ? "sync" : "download",
    label: exporting ? "导出中..." : "导出结果明细",
    onClick: handleExport,
    disabled: exporting,
    primary: true
  }))), React.createElement("div", {
    className: "cards-grid"
  }, summaryCards.map(card => React.createElement(ResultSummaryCard, _extends({
    key: card.key
  }, card)))), quotaExhausted && quotaMessage ? React.createElement(NoticeBox, {
    tone: "warning"
  }, quotaMessage) : null, processingErrorCount > 0 ? React.createElement(NoticeBox, {
    tone: "warning"
  }, "\u68C0\u6D4B\u5230 ", processingErrorCount, " \u6761\u771F\u5B9E\u5F02\u5E38\uFF0C\u8BF7\u901A\u8FC7\u201C\u5BFC\u51FA\u7ED3\u679C\u660E\u7EC6\u201D\u7EE7\u7EED\u6392\u67E5\u3002") : null)), loadingError ? React.createElement(NoticeBox, {
    tone: "error"
  }, loadingError) : null, manualCheckCount > 0 ? React.createElement(ResultActionBanner, {
    icon: "folder_open",
    eyebrow: UI_COPY.pages.analysis.reviewTitle,
    title: `检测到 ${manualCheckCount} 条待人工复核记录`,
    text: UI_COPY.pages.analysis.reviewReady,
    chips: groupedVisible.length > 0 ? React.createElement("div", {
      className: "group-strip group-strip--inline"
    }, groupedVisible.map(group => React.createElement("span", {
      key: group.key,
      className: joinClasses("group-chip", resolveGroupTone(group.key))
    }, React.createElement("span", null, group.label), React.createElement("span", {
      className: "u-tabular"
    }, Number(group.count || group.items.length || 0))))) : null,
    buttonLabel: "\u6253\u5F00\u5F85\u4EBA\u5DE5\u590D\u6838",
    onClick: handleOpenManualCheck,
    disabled: !manualCheckCount
  }) : null, React.createElement(ResultActionBanner, {
    tone: "neutral",
    icon: "folder",
    eyebrow: "\u8F93\u51FA\u76EE\u5F55",
    title: outputPath ? "归档结果与暂存记录已写入输出目录" : "当前还没有可打开的输出目录",
    text: outputPath ? "成功归档文件、暂存记录和导出结果都可以从这里继续查看。" : "待本轮生成结果后，可从这里直接打开输出目录。",
    pathText: outputPath || "",
    buttonLabel: "\u6253\u5F00\u8F93\u51FA\u76EE\u5F55",
    onClick: handleOpenOutput,
    disabled: !outputPath
  })));
}
function App() {
  const [showDisclaimer, setShowDisclaimer] = useState(false);
  return React.createElement(React.Fragment, null, React.createElement(MemoryRouter, null, React.createElement(Routes, null, React.createElement(Route, {
    path: "/",
    element: React.createElement(SettingsPage, {
      onOpenDisclaimer: () => setShowDisclaimer(true)
    })
  }), React.createElement(Route, {
    path: "/processing",
    element: React.createElement(ProcessingPage, {
      onOpenDisclaimer: () => setShowDisclaimer(true)
    })
  }), React.createElement(Route, {
    path: "/analysis",
    element: React.createElement(AnalysisPage, {
      onOpenDisclaimer: () => setShowDisclaimer(true)
    })
  }))), React.createElement(DisclaimerDialog, {
    open: showDisclaimer,
    onClose: () => setShowDisclaimer(false)
  }));
}
const root = createRoot(document.getElementById("root"));
root.render(React.createElement(App, null));
