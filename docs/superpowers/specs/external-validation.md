# External Validation Checklist (Task 12 step 4)

This document records the gates that **cannot** be exercised by local
tests or CI runners and must be re-validated against real systems
before a public release. None of the items below are claimed as
"passed" by the migration — they are deliberately left open until
external execution confirms them.

## 1. Clean Windows 11 x64 MSI install

- Install the signed MSI built by `.github/workflows/windows.yml`
  on a fresh Windows 11 22H2 (or later) machine.
- Confirm the application launches without Python dependencies,
  without any DLL-not-found dialogs, and without a "missing
  component" wizard.
- Confirm WebView2Loader.dll is loaded from the install directory
  (not a system path) and WebView2 runtime version matches the
  `webView2.fixedRuntimeVersion` field of `release.json`.

## 2. DeepSeek live endpoint compatibility

- The migration uses `Microsoft.Extensions.AI.OpenAI` 10.10.0
  pointed at DeepSeek's OpenAI-compatible endpoint. The contract
  for tool calls, JSON mode, and streaming is not covered by
  fixture tests.
- Run an end-to-end `run.start` against the live DeepSeek endpoint
  with the same prompt the parser pipeline uses, then confirm
  every `IChatCompletionResult` re-shapes cleanly into
  `ParserOutcome` records.
- Confirm rate-limit (429) handling produces
  `FailureCategory.Quota` and not `FailureCategory.Network`.

## 3. OCR quality against real receipts

- `Sdcb.SimdPaddleOCR` 1.4.2 OCRs Chinese/English invoices
  directly from PDF bitmaps in the parser pipeline. Fixture
  tests confirm the parser wiring, but not the OCR accuracy
  against real receipts.
- Run a representative sample of 50+ real PDF / image invoices
  through the OCR stage and measure field-extraction accuracy.
- Confirm low-confidence results route to `NeedsManualReview`.

## 4. Real provider browser flows

- The four email-body providers (baiwang, dingtalk, feishu,
  wechat-work) are exercised via `Microsoft.Playwright` 1.62.0
  with offline-cached HTML fixtures in
  `docs/superpowers/fixtures/email-body/`.
- Login-session rotation, captcha handling, and anti-bot
  fingerprinting are out of scope for the migration. Confirm
  each provider still loads in a real browser session before
  shipping a release that depends on it.

## 5. Code signing and final asset hashes

- `SignedAssetVerifier` rejects unsigned production binaries.
  The CI workflow signs the MSI on a Windows runner using the
  project certificate in `signing/`. Confirm the publisher name
  on the MSI matches the certificate subject.
- `release.json.manifestFingerprint` is computed by
  `build/release-manifest.ps1` and verified at install time by
  `ReleaseManifestVerifier`. Confirm the fingerprint committed
  to the release contract matches the one emitted by the
  tagged build.

## 6. Uninstall + upgrade preserves user data

- Install the v1 MSI, populate settings + accounts + a
  completed run. Uninstall without removing the per-user
  application data folder. Install the v2 MSI on top.
- Confirm: the SQLite DB is migrated forward, the audit log
  is preserved, the user must re-enter secrets that were
  imported from the legacy importer.

## What is *not* on this list

- Every other behaviour documented in
  `docs/superpowers/specs/2026-09-23-dotnet10-migration-design.md`
  has a fixture in `docs/superpowers/fixtures/` and a unit or
  integration test that runs locally on every CI invocation.
  The items above are the residual external gates that cannot
  be substituted by an offline test.