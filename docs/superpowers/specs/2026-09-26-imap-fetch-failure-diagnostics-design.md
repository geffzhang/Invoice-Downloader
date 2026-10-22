# IMAP Fetch Failure Diagnostics

## Status

Design approved by the user for review before implementation.

## Context

MailKit fetches selected message UIDs independently and retries a failed UID once. After both attempts fail, `MailboxScanResult.FetchFailures` contains the UID and the fixed safe reason code `IMAP_MESSAGE_FETCH_FAILED`. Successful UIDs remain available, and `HighestUid` advances over successful and failed UIDs. These failures are not run-level failures, but are not currently visible to the user.

## Goal

Show per-UID fetch failures in the Processing Center as non-terminal diagnostics. Keep the successful-message and run-terminal contracts unchanged.

## Data Flow

1. `MailboxScanCaptureNode` retains the fetch failures from the captured `MailboxScanResult`.
2. `PipelineRun` exposes that captured diagnostic list to `RunExecutionService`.
3. Subsequent persisted `run.progress` payloads carry an optional list of `{ uid, reasonCode }` diagnostics. The same list remains in the latest progress snapshot so reconnect/replay presents the same information.
4. The Processing Center renders a warning with the failure count and each UID/safe reason code.

## Contracts And Safety

- The progress property is optional and defaults to an empty list, preserving compatibility with older serialized progress snapshots.
- Diagnostic values contain only numeric UID and the fixed safe reason code. Do not include message content, mailbox credentials, source URLs, query strings, or exception text.
- Do not add these diagnostics to `RunFailure`, terminal `FailureReasonCounts`, or candidate error counters.
- `RunTerminalStatus`, UID cursor advancement, successful message processing, and archive decisions remain unchanged.

## Verification

- Unit-test scan capture of per-UID diagnostics.
- Verify a completed run persists and publishes the diagnostics on progress events while retaining its existing terminal status.
- Verify progress snapshot mapping and Processing Center rendering, including empty and populated states.
- Run the focused Application, App, and Infrastructure test slices, then the full .NET solution and retained Python whitelist.
- Update the desktop parity matrix only after the focused and full verification pass.