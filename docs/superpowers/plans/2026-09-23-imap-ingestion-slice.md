# IMAP Ingestion Vertical Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve saved mailbox credentials, scan IMAP incrementally with MailKit, and emit attachment candidates classified with the Python-compatible tier and prefilter rules.

**Architecture:** Deterministic classification remains in Application and protocol/EF/DPAPI work remains in Infrastructure. `MailKitMailboxScanner` depends on narrow account-reader and mail-session boundaries so rules and UID behavior can be tested without public network access.

**Tech Stack:** .NET 10, C# 14, xUnit, FluentAssertions, EF Core SQLite 10.0.12, MailKit 4.18.0, SkiaSharp 4.154.0-preview.1.26454.9.

## Global Constraints

- Explicit host, port, and TLS values saved on an account are authoritative.
- Known channel capabilities cover `qq.com` and `163.com`; only 163 requires IMAP `ID`.
- Secret plaintext must not enter DTO metadata, logs, persistence, or public exceptions.
- UID cursors are reused only when request and server UIDVALIDITY match.
- This slice excludes ZIP expansion, URL extraction/recovery, pipeline node registration, UI bootstrap registration, and public-network integration tests.
- Do not create a Git commit unless the user explicitly requests one.

---

### Task 1: Message Tier and Attachment Candidate Policy

**Files:**
- Create: `src/InvoiceFlowAI.Application/Mail/EmailTierClassifier.cs`
- Create: `src/InvoiceFlowAI.Application/Mail/AttachmentCandidatePolicy.cs`
- Modify: `src/InvoiceFlowAI.Application/Mail/MailboxScanner.cs`
- Create: `tests/InvoiceFlowAI.Application.Tests/Mail/EmailTierClassifierTests.cs`
- Create: `tests/InvoiceFlowAI.Application.Tests/Mail/AttachmentCandidatePolicyTests.cs`

**Interfaces:**
- Produces: `IEmailTierClassifier.Classify(string? sender, string? subject, string? bodyText) -> int`.
- Produces: `IAttachmentCandidatePolicy.Classify(AttachmentCandidateInput input) -> AttachmentCandidateDecision`.
- Produces: `MailboxAttachmentCandidate`, added to `MailboxScanResult.Attachments`.

- [ ] **Step 1: Add failing tier-classifier tests**

Cover a display-name sender from `12306.cn`, mixed-case `Invoice` in the subject, a Chinese body keyword, and an unrelated tier-four message. Assert tiers 1, 2, 3, and 4 respectively.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~EmailTierClassifierTests
```

Expected: compilation fails because `EmailTierClassifier` does not exist.

- [ ] **Step 3: Implement the tier classifier**

Use `MailboxAddress.TryParse` only if Application already references MimeKit; otherwise parse the address by taking text between `<` and `>` and the substring after the final `@`. Keep the exact domain and keyword sets from the approved design and use `StringComparison.OrdinalIgnoreCase`.

```csharp
public interface IEmailTierClassifier
{
    int Classify(string? sender, string? subject, string? bodyText);
}

public sealed class EmailTierClassifier : IEmailTierClassifier
{
    public int Classify(string? sender, string? subject, string? bodyText);
}
```

- [ ] **Step 4: Run tier tests and verify GREEN**

Run the command from Step 2. Expected: all tier tests pass.

- [ ] **Step 5: Add failing attachment-policy tests**

Test these independent behaviors using supplied image dimensions rather than decoding in Application:

- 2x2 image at 4 KiB is `A/drop/A_EXTREME_NEGATIVE_SIGNAL`.
- Inline `logo.png` has two strong signals and is `A/drop/A_TWO_STRONG_NEGATIVE_SIGNALS`.
- Payload over 5 MiB is `B/retain_only/B_ATTACHMENT_OVER_5MB_RETAIN`.
- Tier-four image with unavailable QR detection is `B/retain_only/B_TIER4_IMAGE_QR_UNKNOWN_RETAIN`.
- Ordinary PDF is `B/main_chain/B_ATTACHMENT_MAIN_CHAIN`.

- [ ] **Step 6: Run attachment-policy tests and verify RED**

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~AttachmentCandidatePolicyTests
```

Expected: compilation fails because the policy types do not exist.

- [ ] **Step 7: Add candidate contracts and minimal policy**

Add these records beside the scanner contract so Infrastructure can return them without leaking MailKit types:

```csharp
public sealed record AttachmentImageInfo(int Width, int Height);

public sealed record AttachmentCandidateInput(
    string FileName,
    long ContentLength,
    int EmailTier,
    string ContentType,
    string ContentDisposition,
    string SourceKind,
    AttachmentImageInfo? ImageInfo = null,
    bool? HasQrCode = null);

public sealed record AttachmentCandidateDecision(
    string Bucket,
    string Action,
    string ReasonCode,
    IReadOnlyList<string> StrongNegativeSignals,
    IReadOnlyList<string> WeakNegativeSignals,
    string? ExtremeNegativeSignal,
    AttachmentImageInfo? ImageInfo);

public sealed record MailboxAttachmentCandidate(
    string Mailbox,
    string MessageUid,
    string FileName,
    string ContentType,
    string ContentDisposition,
    ReadOnlyMemory<byte> Payload,
    int EmailTier,
    AttachmentCandidateDecision Decision);
```

Extend `MailboxScanResult` with `IReadOnlyList<MailboxAttachmentCandidate> Attachments`. Implement the exact thresholds and reason codes in the approved design. Treat `.jpg`, `.jpeg`, `.png`, `.gif`, `.bmp`, and `.webp` as images; use filename keywords `logo`, `banner`, `icon`, `footer`, `header`, `signature`, `avatar`, `wechat`, `weixin`, `qrcode_logo`, `thumb`, and `sprite`.

- [ ] **Step 8: Run all Application tests**

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj
```

Expected: all tests pass.

---

### Task 2: Account Reader and Channel Capabilities

**Files:**
- Create: `src/InvoiceFlowAI.Application/Mail/MailboxAccountReader.cs`
- Create: `src/InvoiceFlowAI.Application/Mail/MailboxChannelRegistry.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Entities/MailboxAccountRow.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/InvoiceFlowDbContext.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfMailboxAccountStore.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/20260923161418_AddMailboxDefaultMailbox.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/20260923161418_AddMailboxDefaultMailbox.Designer.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/InvoiceFlowDbContextModelSnapshot.cs`
- Create: `tests/InvoiceFlowAI.Application.Tests/Mail/MailboxChannelRegistryTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/MailboxAccountRevisionTests.cs`

**Interfaces:**
- Produces: `IMailboxAccountReader.FindAsync(string accountId, CancellationToken) -> MailboxConnectionSettings?`.
- Produces: `IMailboxChannelRegistry.Resolve(string emailAddress) -> MailboxChannelCapabilities`.
- Consumes: existing `InvoiceFlowDbContext` and `MailboxAccountRow`.

- [ ] **Step 1: Add failing account-read tests**

Save an account with `DefaultMailbox = "Invoices"` through the existing store, commit the unit of work, then assert `FindAsync` returns the account ID, email, host, port, TLS, credential name, and default mailbox. Add a missing-ID test that returns `null`, and verify updating an account preserves the newly supplied default mailbox.

- [ ] **Step 2: Run the focused persistence tests and verify RED**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~MailboxAccountRevisionTests
```

Expected: compilation fails because `FindAsync` and `IMailboxAccountReader` do not exist.

- [ ] **Step 3: Add the reader contract and EF implementation**

```csharp
public sealed record MailboxConnectionSettings(
    string AccountId,
    string EmailAddress,
    string ImapHost,
    int ImapPort,
    bool UseTls,
    string CredentialName,
    string? DefaultMailbox);

public interface IMailboxAccountReader
{
    Task<MailboxConnectionSettings?> FindAsync(string accountId, CancellationToken cancellationToken);
}
```

Make `EfMailboxAccountStore` implement the interface and use `AsNoTracking()` with a projection. Add nullable `DefaultMailbox` persistence to `MailboxAccountRow`, its EF configuration, account create/update projections, a generated migration, and the model snapshot. Do not expose revision, masked display fields, or secret plaintext through this reader.

- [ ] **Step 4: Run persistence tests and verify GREEN**

Run the command from Step 2. Expected: all mailbox account tests pass.

- [ ] **Step 5: Add failing channel-registry tests**

Assert `alice@qq.com` resolves to QQ without `ID`, `alice@163.com` resolves to 163 with `ID`, matching is case-insensitive, and an unknown domain returns capabilities with `RequiresIdCommand == false` rather than overriding saved connection settings.

- [ ] **Step 6: Implement and verify the channel registry**

```csharp
public sealed record MailboxChannelCapabilities(string Domain, string DisplayName, bool RequiresIdCommand);

public interface IMailboxChannelRegistry
{
    MailboxChannelCapabilities Resolve(string emailAddress);
}
```

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~MailboxChannelRegistryTests
```

Expected: all channel tests pass.

---

### Task 3: MailKit Incremental Scanner

**Files:**
- Create: `src/InvoiceFlowAI.Infrastructure/Mail/IMailboxSession.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Mail/MailKitMailboxSession.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Mail/MailKitMailboxScanner.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Mail/MailboxScanException.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Mail/MailKitMailboxScannerTests.cs`

**Interfaces:**
- Consumes: `IMailboxAccountReader`, `ISecretStore`, `IMailboxChannelRegistry`, `IEmailTierClassifier`, and `IAttachmentCandidatePolicy`.
- Produces: `IMailboxScanner.ScanAsync` implementation.
- Internal test seam: `IMailboxSessionFactory.Create()` and `IMailboxSession` methods for connect, authenticate, ID, open, search, fetch, and disconnect.

- [ ] **Step 1: Add failing scanner orchestration tests**

Use in-memory fakes for all dependencies. Cover:

- Missing account maps to `MAILBOX_ACCOUNT_NOT_FOUND`.
- Empty credential name or missing secret maps to `CREDENTIALS_NOT_CONFIGURED`.
- A 163 account sends `ID`; a QQ account does not.
- Configured mailbox is selected, otherwise `INBOX`.
- Matching UIDVALIDITY searches after `SinceUid`.
- Changed UIDVALIDITY ignores `SinceUid`, retains `SinceDate`, and sets `UidValidityChanged`.
- MIME attachments are tiered, image dimensions are supplied by the Infrastructure decoder, and policy decisions are projected with immutable payloads.
- MailKit authentication rejection maps to `IMAP_LOGIN_FAILED` without the email or secret in the public message.
- Cancellation propagates as `OperationCanceledException`.

- [ ] **Step 2: Run scanner tests and verify RED**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~MailKitMailboxScannerTests
```

Expected: compilation fails because scanner and session types do not exist.

- [ ] **Step 3: Implement stable scan errors**

```csharp
public sealed class MailboxScanException : Exception
{
    public MailboxScanException(string reasonCode, string safeMessage, Exception? innerException = null)
        : base(safeMessage, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
```

Use reason codes `MAILBOX_ACCOUNT_NOT_FOUND`, `CREDENTIALS_NOT_CONFIGURED`, `IMAP_LOGIN_FAILED`, and `IMAP_PROTOCOL_FAILED`. Safe messages must not interpolate email addresses, hostnames, mailbox names, server responses, or exception messages.

- [ ] **Step 4: Implement the narrow session seam and scanner orchestration**

Session DTOs contain only protocol-neutral values:

```csharp
internal sealed record MailboxSessionInfo(long UidValidity);
internal sealed record MailboxSearchCriteria(long? SinceUid, DateOnly? SinceDate);
internal sealed record MailboxFetchedAttachment(
    string FileName,
    string ContentType,
    string ContentDisposition,
    byte[] Payload);
internal sealed record MailboxFetchedMessage(
    long Uid,
    DateTimeOffset SentAtUtc,
    string Subject,
    string FromAddress,
    string BodyText,
    IReadOnlyList<MailboxFetchedAttachment> Attachments);
```

The scanner must disconnect in `finally`, preserve cancellation, convert protocol/authentication failures to safe `MailboxScanException` values, compute `HighestUid` from fetched messages or retain the request UID when no newer message exists and UIDVALIDITY matches, and return the server UIDVALIDITY using invariant decimal formatting.

- [ ] **Step 5: Implement the MailKit session**

Use `SecureSocketOptions.SslOnConnect` when `UseTls` is true and `SecureSocketOptions.None` otherwise. Authenticate with email and authorization code. For 163, call `ImapClient.Id` with a fixed client identification dictionary that contains product name and version but no user data. Open the requested folder read-only. Build the search query from UID and delivery/internal date constraints, then fetch envelope/body structure/body parts needed for candidate extraction.

Extract body text from `TextBody` with HTML tags removed only for keyword classification; never return body text from the public scanner result. Enumerate MIME attachments and inline parts with filenames. Use SkiaSharp to decode dimensions without mutating payload bytes.

- [ ] **Step 6: Run scanner tests and repair until GREEN**

Run the command from Step 2. Expected: all scanner tests pass with no network access.

- [ ] **Step 7: Run all Infrastructure tests**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj
```

Expected: all tests pass.

---

### Task 4: Contract and Solution Regression Verification

**Files:**
- Modify only files required to resolve compile breaks caused by the intentional `MailboxScanResult` contract extension.

**Interfaces:**
- Consumes: all types introduced in Tasks 1-3.
- Produces: a solution that builds and whose complete automated test suite passes.

- [ ] **Step 1: Build the solution**

```powershell
dotnet build InvoiceFlowAI.sln --no-restore
```

Expected: build succeeds with zero errors. Fix only call sites affected by this slice.

- [ ] **Step 2: Run the complete test suite**

```powershell
dotnet test InvoiceFlowAI.sln --no-build
```

Expected: all tests pass. Record unrelated pre-existing failures without changing unrelated code.

- [ ] **Step 3: Check diagnostics in changed files**

Use VS Code diagnostics for every changed C# file. Expected: no new errors or warnings attributable to this slice.

- [ ] **Step 4: Review the final diff against the design**

Confirm there is no ZIP expansion, URL recovery, pipeline-node registration, UI bootstrap wiring, secret logging, public-network test, or unrelated formatting churn.