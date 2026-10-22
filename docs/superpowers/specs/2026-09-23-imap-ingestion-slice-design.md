# IMAP Ingestion Vertical Slice Design

## Goal

Implement the first runnable C# mailbox-ingestion slice: resolve a saved mailbox account and its DPAPI-protected authorization code, scan IMAP incrementally with MailKit, classify messages with the Python four-tier funnel, and return attachment candidates that downstream nodes can consume.

This slice does not implement ZIP expansion, URL extraction or recovery, pipeline node registration, UI bootstrap dependency injection, or live-provider integration tests.

## Architecture

The Application layer owns stable boundaries and deterministic business rules. The Infrastructure layer owns EF Core account reads, DPAPI secret access, and MailKit protocol operations.

### Application boundaries

- `IMailboxAccountReader` resolves a non-secret mailbox connection descriptor by account ID.
- `MailboxChannelRegistry` describes known channel capabilities. It includes `qq.com` and `163.com`; 163 requires the IMAP `ID` command. Explicit host, port, and TLS values saved on the account remain authoritative.
- `EmailTierClassifier` implements the four-level funnel:
  1. Known sender domain.
  2. Invoice-related subject keyword.
  3. Invoice-related body keyword.
  4. No positive signal.
- `AttachmentCandidatePolicy` returns a deterministic bucket, action, reason code, and normalized signal set.
- `IMailboxScanner` remains the caller-facing scanner abstraction.

### Infrastructure components

- `EfMailboxAccountStore` implements account lookup by ID without returning secret plaintext.
- `MailKitMailboxScanner` loads the authorization code from `ISecretStore`, creates a MailKit session, authenticates, selects the configured mailbox or `INBOX`, and performs the incremental scan.
- A narrow injectable MailKit session boundary permits deterministic protocol tests without public network access. It is not a general-purpose mail abstraction.
- SkiaSharp reads image dimensions for attachment filtering. QR decoding is not introduced in this slice; unavailable QR detection follows the conservative Python `QR_UNKNOWN_RETAIN` behavior.

## Data Contracts

`MailboxScanResult` is extended with attachment candidates. Each candidate contains:

- Source mailbox and message UID.
- Original file name, MIME type, content disposition, and immutable payload bytes.
- Email tier.
- Candidate bucket (`A` or `B`).
- Candidate action (`drop`, `retain_only`, or `main_chain`).
- Stable prefilter reason code.
- Strong and weak negative signals plus an optional extreme signal.
- Optional image dimensions.

Dropped candidates remain represented in the scan result for auditability, but downstream collection consumes only `main_chain` candidates. `retain_only` candidates remain available for later evidence retention and manual review.

The existing `DocumentCandidate` is not expanded with payload bytes. Conversion from a mailbox attachment candidate to a persisted `DocumentCandidate` belongs to the later collect-candidates node.

## Incremental Scan Semantics

1. Resolve the account by `MailboxScanRequest.AccountId`.
2. Resolve its logical credential name through `ISecretStore`.
3. Connect using the saved host, port, and TLS setting.
4. Authenticate with the saved email address and authorization code.
5. Send IMAP `ID` when the resolved channel capability requires it.
6. Open the configured default mailbox, falling back to `INBOX`.
7. Compare server UIDVALIDITY with the request value.
8. If UIDVALIDITY matches, search after `SinceUid` and apply `SinceDate` when present.
9. If UIDVALIDITY differs, set `UidValidityChanged` and ignore the stale UID cursor; retain the date boundary to avoid an unbounded historical scan.
10. Fetch matching messages, classify each message, inspect MIME attachments, and return the highest observed UID and current UIDVALIDITY.

Cancellation is honored during account lookup, secret lookup, connect, authenticate, search, fetch, and disconnect.

## Classification Rules

The tier-one sender domains are migrated exactly from Python: `12306.cn`, `rails.com.cn`, `didichuxing.com`, `gaode.com`, `marriott.com`, `hworld.com`, `cits.com`, `meituan.com`, `carlsonwagonlit.com`, `mycwt.com`, and `citsgbt.com`.

Subject keywords are `发票`, `行程单`, `账单`, `receipt`, and `invoice`. Body keywords are `发票`, `报销`, `行程`, and `差旅`. Matching is case-insensitive.

The attachment policy preserves the Python thresholds and reason codes:

- Tracking pixels or two distinct strong negative signals: bucket A, `drop`.
- Payloads over 5 MiB: bucket B, `retain_only`.
- ZIP container failure or filtered-member fallback: bucket B, `retain_only`.
- Tier-four images without a confirmed QR code: bucket B, `retain_only`.
- Low-confidence images: bucket B, `retain_only`.
- Other attachments: bucket B, `main_chain`.

Strong signals include inline image, image under 10 KiB, decorative file name, and image dimensions no larger than 32 by 32. Images under 50 KiB are a weak signal. Images no larger than 2 by 2 and no larger than 4 KiB are tracking pixels.

## Errors and Security

Stable scanner failures distinguish:

- Account not found.
- Credential not configured or missing.
- IMAP authentication rejected.
- IMAP connection or protocol failure.

Public error objects contain only a reason code and safe message. Authorization codes, body text, payload bytes, full server responses, and unsanitized exception details never enter logs, DTO metadata, or persistence. Secret plaintext remains local to the scanner call and is not retained after authentication.

## Testing Strategy

Implementation follows red-green-refactor in these increments:

1. Tier classifier tests for sender parsing, case-insensitive subject/body matching, and fallback.
2. Attachment policy tests for tracking pixels, two strong signals, size limits, tier-four images, and main-chain documents.
3. Account-reader tests for found and missing accounts.
4. Scanner tests through the injectable session boundary for account and secret resolution, mailbox selection, UIDVALIDITY reset, UID/date search, 163 `ID`, MIME projection, cancellation, and safe error mapping.
5. Focused Application and Infrastructure test projects, followed by the full solution test suite.

No test depends on a real QQ or 163 mailbox.

## Acceptance Criteria

- A saved account can be resolved by ID and authenticated through its DPAPI credential reference.
- QQ and 163 channel capabilities resolve deterministically, including 163 `ID` behavior.
- Matching messages are scanned incrementally without reusing a stale UID cursor after UIDVALIDITY changes.
- Every MIME attachment receives a deterministic tier and candidate decision.
- Main-chain attachment candidates include immutable payload bytes and enough source metadata for a later collect-candidates node.
- Missing accounts, credentials, authentication failures, cancellation, and protocol failures have tested behavior and do not leak secrets.
- Focused and full automated test suites pass.