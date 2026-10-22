# Settings Form RPC Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the settings page's legacy settings, credential, connection-test, and directory-picker calls with typed Avalonia RPC while preserving the current single-mailbox workflow and remember-settings behavior.

**Architecture:** Keep the existing UI layout and move settings/account/secret validation into Application services. Persist non-secret settings and mailbox accounts through the Infrastructure SQLite/EF boundary; persist remembered secrets with DPAPI and unremembered secrets only in a process-lifetime session store. Register explicit typed RPC handlers in the App composition root, use Avalonia's native folder picker for directory selection, and migrate only the settings operations in `index_app.js`; task start/progress/stop/results remain on their existing path until later plans.

**Tech Stack:** .NET SDK 10.0.100, `net10.0-windows`, Avalonia 12.1.0, Avalonia.Controls.WebView 12.1.0, EF Core SQLite 10.0.12, xUnit, FluentAssertions, Node.js built-in test runner.

## Global Constraints

- Target Windows 11 x64 with `net10.0-windows` and `win-x64` for release validation.
- Use .NET SDK 10.0.100 with `rollForward=disable`; CI uses Windows Server 2025 x64.
- Keep Avalonia and Avalonia.Controls.WebView at exactly 12.1.0 and EF Core/SQLite at exactly 10.0.12; do not add floating package versions.
- Use RPC protocol `invoiceflow.rpc.v1`, camelCase JSON, strict DTOs, and stable error codes; do not register legacy `pywebview.api` method names.
- Never return, log, persist in UI storage, or serialize a secret into settings/account/run DTOs. `session` secrets live only in process memory; `persistent` secrets use DPAPI CurrentUser.
- Preserve current company matching and remember-settings behavior. Add non-secret `CompanyName` and `LastOutputDirectory` fields to the settings contract and persistence model.
- Do not migrate task start, progress, cancellation, or result operations in this plan. No UI state may imply `run.start` is backed by the new RPC yet.
- Follow TDD per task. Do not commit unless the user explicitly requests it.

---

## File Map

**Contracts:** `src/InvoiceFlowAI.Contracts/Settings/`, `Accounts/`, and `Rpc/` hold serialized settings and operation DTOs. New DTOs define account listing, provider credential tests, secret retention, and directory-picker results.

**Application:** `src/InvoiceFlowAI.Application/Persistence/` and focused settings/credentials services define storage and behavior interfaces without EF, DPAPI, or Avalonia dependencies.

**Infrastructure:** `src/InvoiceFlowAI.Infrastructure/Persistence/` owns EF entities, migrations, and stores; `Security/` owns DPAPI/session-secret implementations and provider credential resolution; the service registration extension wires implementations.

**Desktop RPC:** `src/InvoiceFlowAI.App/Rpc/` contains handlers and composition; the folder-picker adapter is in `src/InvoiceFlowAI.App/` and is the only part allowed to access Avalonia controls.

**Frontend:** `templates/index_app.js` switches settings load/save, account/provider tests, secret set/delete, and folder selection to `RpcClient`; `start_processing` and all run/results calls are deliberately unchanged in this phase.

**Tests:** Add focused tests under `tests/InvoiceFlowAI.Contracts.Tests/`, `tests/InvoiceFlowAI.Application.Tests/`, `tests/InvoiceFlowAI.Infrastructure.Tests/`, `tests/InvoiceFlowAI.App.Tests/Rpc/`, and JavaScript RPC/UI tests under `tests/InvoiceFlowAI.App.Tests/Rpc/`.

---

### Task 1: Define Settings Slice RPC Contracts

**Files:**
- Modify: `src/InvoiceFlowAI.Contracts/Settings/SettingsUpdateRequest.cs`
- Modify: `src/InvoiceFlowAI.Contracts/Settings/UserSettingsSnapshot.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/SettingsSnapshotRpcHandlerTests.cs`
- Create: `src/InvoiceFlowAI.Contracts/Accounts/AccountListResult.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/SecretSetRequest.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/SecretMutationResult.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/ProviderTestRequest.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/ProviderTestResult.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/DirectoryChooseResult.cs`
- Test: `tests/InvoiceFlowAI.Contracts.Tests/SettingsRpcContractTests.cs`
- Test: `tests/InvoiceFlowAI.Contracts.Tests/ContractRoundTripTests.cs`

**Interfaces:**
- `UserSettingsSnapshot` appends `CompanyName = ""` and nullable `LastOutputDirectory = null` as optional trailing constructor parameters so existing positional call sites and old JSON snapshots remain readable.
- `SettingsUpdateRequest` adds nullable `CompanyName` and `LastOutputDirectory` patch properties.
- `SecretSetRequest` carries `Name`, `Value`, and a closed retention enum with `Persistent` and `Session` values. Its result contains only `Name`, `Configured`, and `Persistent`.
- `ProviderTestRequest` carries a provider identifier and credential logical name, never a secret value. Its result contains success and a safe message/code only.
- `DirectoryChooseResult` distinguishes user cancellation from a selected normalized path.
- Account listing returns a collection of existing `MailboxAccountSnapshot` values; it does not add secret material.

- [x] **Step 1: Add failing strict JSON round-trip tests** for new snapshot fields, settings patch fields, both secret retention enum values, provider test, account list, and directory cancellation/success results. Assert serialized account/settings/provider results contain neither `authCode`, `apiKey`, nor secret values; `SecretSetRequest.Value` exists only on the inbound write DTO. Add a legacy snapshot JSON fixture without the new optional fields and assert it loads with empty company and null output directory.
- [x] **Step 2: Run the focused Contracts test project** with `dotnet test tests/InvoiceFlowAI.Contracts.Tests/InvoiceFlowAI.Contracts.Tests.csproj --no-restore`. Confirm the added type references fail to compile or round-trip before implementation.
- [x] **Step 3: Add the DTOs and enum** with nullable semantics matching patch behavior and strict serializer conventions from `InvoiceJsonOptions`.
- [x] **Step 4: Re-run the focused Contracts project** and confirm all new and existing tests pass.

### Task 2: Add Durable Non-Secret Settings Persistence

**Files:**
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Entities/UserSettingsRow.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/InvoiceFlowDbContext.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/20260925120000_AddUserSettingsCompanyAndOutputDirectory.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/InvoiceFlowDbContextModelSnapshot.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/SqliteTestFixture.cs`
- Create: `src/InvoiceFlowAI.Application/Persistence/IUserSettingsStore.cs`
- Create: `src/InvoiceFlowAI.Application/Persistence/SettingsRevisionConflictException.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfUserSettingsStore.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/UserSettingsStoreTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/InitialSchemaTests.cs`

**Interfaces:**
- `IUserSettingsStore.LoadAsync(CancellationToken)` returns the current `UserSettingsSnapshot`.
- `IUserSettingsStore.UpdateAsync(SettingsUpdateRequest, CancellationToken)` validates expected revision, persists the patch and returns the updated snapshot.
- The EF store uses the existing `IUnitOfWorkFactory` and `TransactionPurpose.SettingsUpdate`; revision conflicts map to `SETTINGS_REVISION_CONFLICT`.
- Fingerprints are computed from canonical behavior-affecting non-secret settings, including company matching, excluding the remembered output-directory preference, credential names, credential state, DPAPI ciphertext, and session values.
- `IUserSettingsStore` owns only SQLite data and exposes an idempotent `ImportSnapshotAsync(UserSettingsSnapshot, CancellationToken)` operation for the App-layer bootstrap to import the current JSON snapshot without introducing an Application-to-App dependency.
- The store operates on the singleton `UserSettings` row created by the default RuleSet bootstrap. App startup runs database migrations, then `RuleSetBootstrapper.EnsureDefaultAsync`, then imports the old snapshot before exposing settings RPC.

- [x] **Step 1: Add failing SQLite tests** for default-row load, update of company/output path, revision increment, stale-revision rejection, and fingerprint stability when only non-fingerprint metadata changes.
- [x] **Step 2: Run** `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~UserSettingsStoreTests`; confirm expected missing member/store failures.
- [x] **Step 3: Add the row properties and migration** without changing unrelated schema. Update `SqliteTestFixture.ResetAsync` with the migration ID and apply migrations through the existing fixture path. Add a failing test that imports a supplied non-secret `UserSettingsSnapshot` once and never overwrites an existing SQLite row or an existing `LegacyImportState` marker.
- [x] **Step 4: Implement `IUserSettingsStore` and its EF store**, using strict settings JSON converters already used by the Infrastructure project and the repository's canonical fingerprint utility.
- [x] **Step 5: Re-run the focused test filter**, then run `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~InitialSchemaTests`.

### Task 3: Add Account Listing and Application Mutations

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Mail/MailboxAccountReader.cs`
- Create: `src/InvoiceFlowAI.Application/Persistence/IMailboxAccountStore.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfMailboxAccountStore.cs`
- Create: `src/InvoiceFlowAI.Application/Accounts/AccountApplicationService.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/MailboxAccountRevisionTests.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Accounts/AccountApplicationServiceTests.cs`

**Interfaces:**
- Extend `IMailboxAccountReader` with `ListAsync(CancellationToken)` returning only account snapshots.
- `IMailboxAccountStore` extends the reader contract with `SaveAsync(MailboxAccountDraft, int expectedRevision, IUnitOfWork, CancellationToken)`; Application services depend on this abstraction, not the EF implementation.
- `AccountApplicationService.ListAsync` and `SaveAsync(AccountSaveRequest, CancellationToken)` validate account fields and expected revision, coordinate a single UoW, and return typed snapshots.
- `CredentialConfigured` is derived by checking the referenced name through `ISecretStore`; a non-empty `CredentialName` alone is not evidence that a secret exists or can be decrypted.
- Creating the single mailbox account requires `ExpectedRevision == 0`; updates require the currently stored revision. Raw auth code is not accepted by these methods.

- [x] **Step 1: Add failing tests** for ordered empty/list responses, account creation at revision zero, update revision increment, stale write conflict, and rejection of malformed account endpoints/credential logical names.
- [x] **Step 2: Run** `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~AccountApplicationServiceTests`; confirm the service/list API is absent.
- [x] **Step 3: Add `ListAsync` to the reader, define `IMailboxAccountStore`, implement it in the EF store,** and implement the application service with allowlisted credential names and field validation.
- [x] **Step 4: Re-run the focused Application tests** and `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~MailboxAccountRevisionTests`.

### Task 4: Implement Persistent and Session Secret Lifetimes

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Persistence/ISecretStore.cs`
- Create: `src/InvoiceFlowAI.Application/Security/ISecretRetentionStore.cs`
- Create: `src/InvoiceFlowAI.Application/Security/SecretApplicationService.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Security/SessionSecretStore.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Security/CompositeSecretStore.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAIInfrastructureServiceCollectionExtensions.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Security/SecretApplicationServiceTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Security/SessionSecretStoreTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Security/DpapiSecretStoreTests.cs`

**Interfaces:**
- `ISecretRetentionStore.SetAsync(SecretSetRequest, CancellationToken)` selects persistent DPAPI or session-only storage and returns `SecretMutationResult`.
- `ISecretStore.GetAsync` resolves a session value first and then a DPAPI value; deletion clears both layers.
- Turning remember-settings off deletes the matching persistent secret before accepting a session-only replacement. Turning it on promotes a supplied session value to DPAPI within the host without returning it to JavaScript.
- Secret names are allowlisted for the IMAP authorization code and configured GLM provider key; values are never logged.

- [x] **Step 1: Add failing tests** for session values disappearing with a new store instance, persistent values surviving via a fake persistent store, session-over-persistent precedence, delete clearing both stores, invalid secret names, and retention-policy transitions.
- [x] **Step 2: Run** `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~SecretApplicationServiceTests`; confirm the new API is missing.
- [x] **Step 3: Implement the in-memory session store and composite resolver** with cancellation support and no disk serialization.
- [x] **Step 4: Implement the application retention policy** including remove-old-persistent-then-save-session behavior when remember is disabled and in-host promotion when enabled.
- [x] **Step 5: Register the security services** without creating a second `ISecretStore` registration and run focused Application/Infrastructure security tests.
- [x] **Step 6: Verify the secret DTO and log boundary** with a serialization assertion and a test logger that confirms secret values are absent from emitted events.

### Task 5: Add Connection-Test Application Services

**Files:**
- Create: `src/InvoiceFlowAI.Application/Mail/IMailboxConnectionTester.cs`
- Create: `src/InvoiceFlowAI.Application/Ai/IProviderConnectionTester.cs`
- Create: `src/InvoiceFlowAI.Application/Accounts/AccountTestService.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAIInfrastructureServiceCollectionExtensions.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Mail/MailKitMailboxConnectionTester.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Ai/DeepSeekProviderConnectionTester.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Accounts/AccountTestServiceTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Mail/MailKitMailboxConnectionTesterTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Ai/DeepSeekProviderConnectionTesterTests.cs`

**Interfaces:**
- `IMailboxConnectionTester.TestAsync(MailboxConnectionSettings, string credential, string? mailbox, CancellationToken)` returns a typed safe result.
- `IProviderConnectionTester.TestAsync(string providerId, string credential, CancellationToken)` returns a typed safe result.
- `AccountTestService` resolves account metadata and secret values internally, validates provider/name pairing, invokes exactly one tester, and maps transport/auth failures to stable safe codes/messages.

- [x] **Step 1: Add fake-based failing tests** proving IMAP testing resolves the account's credential reference, GLM testing resolves only its configured key, missing credentials fail before network access, and test errors contain no credential or raw exception text.
- [x] **Step 2: Run** `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~AccountTestServiceTests`.
- [x] **Step 3: Implement the Application service and interfaces**; map cancellation and expected connection failures without leaking exception strings.
- [x] **Step 4: Implement Infrastructure testers** using the existing MailKit and DeepSeek adapters; tests must inject fake transport/client boundaries rather than contact external services.
- [x] **Step 5: Run focused Application and Infrastructure test filters** for `AccountTestServiceTests`, `MailKitMailboxConnectionTesterTests`, and `DeepSeekProviderConnectionTesterTests`.

### Task 6: Register Typed Settings RPC Handlers and Native Directory Picker

**Files:**
- Create: `src/InvoiceFlowAI.App/Rpc/SettingsGetRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/SettingsUpdateRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/AccountListRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/AccountSaveRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/AccountTestRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/ProviderTestRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/SecretSetRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/SecretDeleteRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/DirectoryChooseRpcHandler.cs`
- Modify: `src/InvoiceFlowAI.App/Rpc/AppRpcComposition.cs`
- Modify: `src/InvoiceFlowAI.App/App.axaml.cs`
- Modify: `src/InvoiceFlowAI.App/Program.cs`
- Modify: `src/InvoiceFlowAI.App/MainWindow.axaml.cs`
- Create: `src/InvoiceFlowAI.App/Settings/LegacySettingsSnapshotImporter.cs`
- Create: `src/InvoiceFlowAI.App/Settings/IDirectoryPicker.cs`
- Create: `src/InvoiceFlowAI.App/Settings/AvaloniaDirectoryPicker.cs`
- Create: `src/InvoiceFlowAI.App/Settings/IMainWindowAccessor.cs`
- Create: `src/InvoiceFlowAI.App/Settings/AvaloniaMainWindowAccessor.cs`
- Create: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/ScopedRpcHandler.cs`
- Modify: `src/InvoiceFlowAI.App/Rpc/RpcDispatcher.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAIInfrastructureServiceCollectionExtensions.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Rpc/SettingsRpcHandlerTests.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Rpc/AppRpcCompositionTests.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Rpc/DirectoryChooseRpcHandlerTests.cs`

**Interfaces:**
- Each handler implements `IRpcHandler` with one explicit method name and a typed params/result DTO.
- `AppRpcComposition` receives service-backed handlers; only `bridge.hello` and currently supported settings methods are advertised as capabilities.
- `WebViewRpcBridge` dispatches on a background thread, so `AvaloniaDirectoryPicker` must marshal `Window.StorageProvider.OpenFolderPickerAsync` through `Dispatcher.UIThread.InvokeAsync`; handler code depends on `IDirectoryPicker` and never touches controls.
- `AppServiceProviderFactory` creates SQLite options from the app-local data path, registers the existing Infrastructure/Application extensions, applies EF migrations before the dispatcher advertises business capabilities, and disposes scopes/providers on window close.
- Startup registration includes the existing `EfRuleSetBootstrapStore`/`RuleSetBootstrapper` path so the `UserSettings` singleton and its RuleSet foreign key exist before settings import/update.
- `ScopedRpcHandler<THandler>` creates one async DI scope per RPC and resolves its inner handler there; the singleton dispatcher must not capture a scoped `DbContext` or scoped service past request completion.
- `AvaloniaMainWindowAccessor` is updated when the main window is created/closed. The folder-picker adapter receives the active owner window through this App-layer accessor, not through a request DTO or a handler-held control reference.
- `settings.snapshot.get` remains temporarily registered until `bridge-bootstrap.js` switches to `settings.get`; remove the snapshot method, JSON snapshot store, and its unused file path only in the same successful cutover.
- `LegacySettingsSnapshotImporter` is App-layer startup glue: when no SQLite settings row exists, it loads the old JSON snapshot and calls `IUserSettingsStore.ImportSnapshotAsync`; it imports non-secret settings only and records completion in `LegacyImportState`.

- [x] **Step 1: Add failing dispatcher tests** for each method's success payload, invalid params, stable revision/credential errors, and unknown-method rejection. Add a fake directory picker test for selected and cancelled outcomes.
- [x] **Step 2: Run** `dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore --filter FullyQualifiedName~SettingsRpcHandlerTests`; verify methods are not registered.
- [x] **Step 3: Add handlers and map application failures** to the existing RPC error envelope; do not include filesystem paths in unexpected exception messages.
- [x] **Step 4: Add Avalonia folder-picker adapter** using `Window.StorageProvider.OpenFolderPickerAsync`; marshal the call to the UI thread, return cancellation explicitly, and normalize only the selected path.
- [x] **Step 5: Wire SQLite, DPAPI, session secrets, Application services, and handler factories** in `AppServiceProviderFactory`. Before `CreateDispatcher` returns, apply migrations, run `LegacySettingsSnapshotImporter` once, then create the dispatcher; do not broaden this task into unrelated release-manifest work.
- [x] **Step 6: Add and test `ScopedRpcHandler<THandler>`** in the dispatcher/composition boundary so scoped account/settings services and DbContexts are disposed after each RPC.
- [x] **Step 7: Add and test `AvaloniaMainWindowAccessor`** lifecycle updates and the directory picker's UI-thread marshal.
- [x] **Step 8: Re-run App RPC and folder-picker tests** and build with `dotnet build src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj --no-restore`.

### Task 7: Switch Settings Page Calls to RPC

**Files:**
- Modify: `templates/index_app.js`
- Modify: `templates/bridge-bootstrap.js`
- Modify: `templates/rpc_client.js` only if a missing request/transport capability is demonstrated by a failing test
- Test: `tests/InvoiceFlowAI.App.Tests/Rpc/settings_page_rpc.test.mjs`
- Test: `tests/InvoiceFlowAI.App.Tests/Rpc/rpc_client.test.mjs`

**Interfaces:**
- Page initialization calls `settings.get` and `account.list` after successful `bridge.hello`.
- Saving non-secret settings calls `settings.update` with the loaded revision. Secret values are sent only to `secret.set`, then cleared from controlled inputs after success; loading never populates a secret field.
- `remember_settings` selects `persistent` or `session` retention. Unchecking it also requests persistent secret deletion and resets persisted non-secret values to defaults while retaining only the current non-secret form draft in session state as allowed by the existing UI behavior.
- Email test calls `account.test`; GLM test calls `provider.test`; folder browse calls `directory.choose`.
- `start_processing`, progress, stop, result, open-folder, invoice-view, and export calls remain unchanged in this plan.

- [x] **Step 1: Add failing Node tests** for bootstrap ordering, calls and params for load/save/account/provider/directory, blank secret fields after load, secret clear-after-save, remember on/off retention values, and no `localStorage`/`sessionStorage` write containing secret values.
- [x] **Step 2: Run** `node --test tests/InvoiceFlowAI.App.Tests/Rpc/settings_page_rpc.test.mjs`; confirm expected failures before changing page calls.
- [x] **Step 3: Replace only the settings method call sites** and update settings state mapping; keep the task and result legacy calls intact.
- [x] **Step 4: Run** `node --test tests/InvoiceFlowAI.App.Tests/Rpc/settings_page_rpc.test.mjs tests/InvoiceFlowAI.App.Tests/Rpc/rpc_client.test.mjs`; compile JSX with the bundled Babel standalone and syntax-check plain JS bridge files.
- [x] **Step 5: Run a UI copy leak audit** for the migration-note terms listed in `AGENTS.md`, manually inspect the changed visible strings, and confirm no implementation notes are rendered.

### Task 8: End-to-End Settings Slice Verification

**Files:**
- Create: `tests/InvoiceFlowAI.App.Tests/EndToEnd/NativeWebViewSettingsIntegrationTests.cs`
- Modify: `src/InvoiceFlowAI.App/WebView/AvaloniaNativeWebViewMessageChannel.cs`
- Modify: `src/InvoiceFlowAI.App/Rpc/WebViewRpcBridge.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/WebViewRpcBridgeTests.cs`
- Verify: `src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj`, `src/InvoiceFlowAI.App/Rpc/`, `templates/`

**Interfaces:**
- A Windows integration test starts the real Avalonia WebView, loads the packaged local page, completes `bridge.hello`, loads settings, and performs a harmless non-secret settings update through the real message channel.
- Credential tests use a fake secret store in the automated integration path; no real user credential or outbound GLM/IMAP call is required.

Current checkpoint: the abstract message-channel round-trip and the opt-in `NativeWebView` integration both pass. The native test launches an Avalonia window, loads the packaged local page, completes `bridge.hello`, loads settings/accounts, and performs a non-secret settings update. RPC responses are dispatched to `window.chrome.webview`, which is the event target used by `RpcClient` in WebView2.

- [x] **Step 1: Add a failing Windows integration assertion** for bridge hello followed by settings load/update using the real WebView message transport.
- [x] **Step 2: Run the focused Windows test** for the message-channel round trip and opt-in native WebView integration; use the native desktop run to verify the packaged page and real message transport.
- [x] **Step 3: Add the integration wiring** without weakening the existing runtime/manifest checks.
- [x] **Step 4: Run final focused validation:**

```powershell
dotnet test tests/InvoiceFlowAI.Contracts.Tests/InvoiceFlowAI.Contracts.Tests.csproj --no-restore
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~Accounts|FullyQualifiedName~Security"
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~UserSettings|FullyQualifiedName~MailboxAccount|FullyQualifiedName~Security|FullyQualifiedName~ConnectionTester"
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore
node --test tests/InvoiceFlowAI.App.Tests/Rpc/*.test.mjs
node --check templates/bridge-bootstrap.js
node --check templates/rpc_client.js
node --check templates/settings_rpc.js
$env:INVOICEFLOW_RUN_NATIVE_WEBVIEW_TEST = "1"
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore --filter FullyQualifiedName~NativeWebViewSettingsIntegrationTests
dotnet build src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj --configuration Release --no-restore
git diff --check
```

- [x] **Step 5: Verify the migration boundary**: the settings route no longer calls legacy load/save/test/directory APIs; no run or result API was migrated; no test fixture or log contains real secrets.

---

## Plan Self-Review

- **Spec coverage:** Covers the single-mailbox form, typed settings/account/provider/secret/directory RPC, company/output settings fields, revision checks, DPAPI/session retention, IMAP and GLM test separation, native directory selection, staged UI cutover, and security/validation checks. Run lifecycle and results are explicitly deferred per the approved spec.
- **Placeholder scan:** No `TODO`/`TBD` or deferred “implement later” steps are used. IMAP and provider tests use injected fake tester/client implementations, so they make no external network calls.
- **Type consistency:** DTO names and properties are defined in Task 1; persistence methods are defined in Task 2; account and secret service interfaces are defined before their RPC handlers consume them. The task intentionally preserves the existing `settings.snapshot.get` handler only until the new `settings.get` handler is registered and tested, then removes the obsolete method in the same cutover task.