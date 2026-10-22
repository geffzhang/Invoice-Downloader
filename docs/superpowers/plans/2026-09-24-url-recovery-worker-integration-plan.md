# URL Recovery Worker and Pipeline Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run provider recovery in a bounded child process, validate all worker outputs before use, and prove the grouped recovery contract is wired through the existing pipeline boundary.

**Architecture:** Add a dedicated `net10.0` console worker that consumes a versioned per-job JSON manifest and writes artifact files plus an atomic result manifest. Infrastructure starts the worker without a shell, enforces timeout/cancellation with process-tree termination, validates paths/hashes/signatures, and always removes the job directory. Pipeline integration records the source-group relationship using the existing `Documents.ProviderGroupKey` column when the C# candidate persistence writer is introduced.

**Tech Stack:** .NET SDK 10.0.401, `System.Text.Json`, `ProcessStartInfo.ArgumentList`, EF Core SQLite, xUnit 2, FluentAssertions, existing URL strategy registry and pinned transport.

**Implementation Status (2026-09-24):** Tasks 1-3 and 5 are implemented and covered by protocol, worker, process-tree, output-validation, cleanup, and EF history tests. Task 4 packaging is implemented and verified by the Windows publish script/release-manifest smoke check; the planned automated publish test was not added. Task 6 is partial: the integration test verifies production worker-client/stage/source-writer composition and selected PDF handling, not mailbox discovery through extraction, pairing, and archive. Commit steps are intentionally not performed. The latest full solution run passed 420 tests.

## Global Constraints

- Keep `global.json` at SDK `10.0.401`, `rollForward: disable`.
- The worker targets `net10.0`; the desktop app and App tests target Windows-specific .NET 10 TFMs.
- Manifests are versioned, bounded, atomically replaced, and contain no diagnostics with raw URLs or exception text.
- Raw capability URLs may exist only in the access-restricted per-job request manifest and process memory; delete them with the job directory.
- Accept output paths only when their fully resolved paths are descendants of the job directory and are regular files.
- Verify protocol version, result count, byte length, SHA-256, and PDF/XML/OFD signature in the parent before returning data to the pipeline.
- Caller cancellation propagates; timeout and worker crash produce distinct safe recovery failures.
- Do not record a completed recovery history alias before the selected artifact's processing is completed and non-retryable.

---

### Task 1: Define the versioned worker protocol (Complete)

**Files:**

- Create: `src/InvoiceFlowAI.Infrastructure/Url/Worker/UrlRecoveryWorkerRequest.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/Worker/UrlRecoveryWorkerResponse.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/Worker/UrlRecoveryWorkerManifestStore.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryWorkerManifestStoreTests.cs`

**Interfaces:**

- Request schema version is `1` and includes one `UrlCandidateGroup`, absolute job directory, response byte cap, artifact count cap, and result manifest path.
- Response schema version is `1` and includes a stable failure code or captured artifact records with relative file path, kind, content type, byte length, and lowercase SHA-256.
- Manifest writes use a sibling temporary file and `File.Move(temp, destination, overwrite: true)` after flush.

- [x] **Step 1: Add tests for schema round-trip, unsupported versions, size limits, and atomic replacement**

```csharp
[Fact]
public async Task Rejects_worker_response_with_unsupported_schema_version()
{
    await WriteResponseAsync(new { schemaVersion = 99, artifacts = Array.Empty<object>() });

    var act = () => manifestStore.ReadResponseAsync(resultPath, CancellationToken.None);

    await act.Should().ThrowAsync<UrlRecoveryWorkerProtocolException>()
        .Where(exception => exception.ReasonCode == "URL_RECOVERY_WORKER_PROTOCOL_INVALID");
}
```

- [x] **Step 2: Run worker protocol tests and confirm the protocol types do not exist**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~UrlRecoveryWorkerManifestStoreTests`

Expected: FAIL because the versioned worker protocol is absent.

- [x] **Step 3: Implement strict JSON parsing and atomic writes**

```csharp
public sealed record UrlRecoveryWorkerRequest(
    int SchemaVersion,
    UrlCandidateGroup Group,
    string JobDirectory,
    int MaxResponseBytes,
    int MaxArtifacts,
    string ResultManifestPath);
```

Reject unknown schema versions, missing required fields, oversized manifests, excessive candidate URLs, and absolute artifact output paths in worker responses. Use fixed safe error codes; do not serialize exception messages.

- [x] **Step 4: Run worker protocol tests**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~UrlRecoveryWorkerManifestStoreTests`

Expected: PASS for valid round-trip and every malformed-manifest case.

- [ ] **Step 5: Commit worker protocol contracts**

```bash
git add src/InvoiceFlowAI.Infrastructure/Url/Worker/UrlRecoveryWorkerRequest.cs src/InvoiceFlowAI.Infrastructure/Url/Worker/UrlRecoveryWorkerResponse.cs src/InvoiceFlowAI.Infrastructure/Url/Worker/UrlRecoveryWorkerManifestStore.cs tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryWorkerManifestStoreTests.cs
git commit -m "feat: define URL recovery worker protocol"
```

### Task 2: Create the child worker executable (Complete)

**Files:**

- Create: `src/InvoiceFlowAI.UrlRecovery.Worker/InvoiceFlowAI.UrlRecovery.Worker.csproj`
- Create: `src/InvoiceFlowAI.UrlRecovery.Worker/Program.cs`
- Create: `src/InvoiceFlowAI.UrlRecovery.Worker/WorkerHost.cs`
- Create: `tests/InvoiceFlowAI.UrlRecovery.Worker.Tests/InvoiceFlowAI.UrlRecovery.Worker.Tests.csproj`
- Create: `tests/InvoiceFlowAI.UrlRecovery.Worker.Tests/WorkerHostTests.cs`
- Modify: `InvoiceFlowAI.sln`

**Interfaces:** `WorkerHost.RunAsync(string requestPath, CancellationToken cancellationToken)` returns `0` only after atomically writing a valid response manifest; malformed input returns a nonzero code without printing request data.

- [x] **Step 1: Add worker-host tests using a fake recovery strategy**

```csharp
[Fact]
public async Task Writes_artifact_under_job_directory_and_returns_only_relative_path()
{
    var exitCode = await workerHost.RunAsync(requestPath, CancellationToken.None);
    var response = await manifestStore.ReadResponseAsync(resultPath, CancellationToken.None);

    exitCode.Should().Be(0);
    Path.IsPathRooted(response.Artifacts.Single().RelativePath).Should().BeFalse();
}
```

- [x] **Step 2: Run the Worker test project and confirm the project does not exist**

Run: `dotnet test tests/InvoiceFlowAI.UrlRecovery.Worker.Tests/InvoiceFlowAI.UrlRecovery.Worker.Tests.csproj`

Expected: FAIL because the project and worker host are not present.

- [x] **Step 3: Implement the console entry point and isolated DI graph**

```csharp
if (args.Length != 2 || args[0] != "--job-manifest") return 64;
return await WorkerHost.RunAsync(args[1], CancellationToken.None);
```

Register the strategy registry and pinned transport directly in the worker host. Do not register the parent `UrlRecoveryWorkerClient` in the child process.

- [x] **Step 4: Run worker tests and build the worker for the pinned SDK**

Run: `dotnet test tests/InvoiceFlowAI.UrlRecovery.Worker.Tests/InvoiceFlowAI.UrlRecovery.Worker.Tests.csproj`

Expected: PASS; the worker does not emit raw request content to stdout/stderr.

- [ ] **Step 5: Commit worker executable and tests**

```bash
git add src/InvoiceFlowAI.UrlRecovery.Worker tests/InvoiceFlowAI.UrlRecovery.Worker.Tests InvoiceFlowAI.sln
git commit -m "feat: add isolated URL recovery worker"
```

### Task 3: Implement the bounded parent worker client (Complete)

**Files:**

- Create: `src/InvoiceFlowAI.Infrastructure/Url/Worker/IUrlRecoveryWorkerProcessRunner.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/Worker/UrlRecoveryWorkerProcessRunner.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/Worker/UrlRecoveryWorkerClient.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryWorkerClientTests.cs`

- [x] **Step 1: Add tests for valid results, timeout, cancellation, worker exit failure, traversal, symlink escape, digest mismatch, and cleanup**

```csharp
[Fact]
public async Task Rejects_worker_output_outside_job_directory_and_cleans_job()
{
    var result = await client.RecoverAsync(group, CancellationToken.None);

    result.Failure!.ReasonCode.Should().Be("URL_RECOVERY_WORKER_OUTPUT_INVALID");
    Directory.Exists(fakeRunner.JobDirectory).Should().BeFalse();
}
```

- [x] **Step 2: Run worker-client tests and confirm the process client is missing**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~UrlRecoveryWorkerClientTests`

Expected: FAIL because there is no worker client or process runner.

- [x] **Step 3: Implement no-shell launch, timeout, cancellation, and output verification**

```csharp
var startInfo = new ProcessStartInfo(workerExecutablePath) { UseShellExecute = false };
startInfo.ArgumentList.Add("--job-manifest");
startInfo.ArgumentList.Add(requestManifestPath);
```

Use `Process.Kill(entireProcessTree: true)` on timeout/cancellation; wait for exit before deleting the job directory. Validate file path confinement after resolving symlinks, then verify length, SHA-256, and format signature. Return only safe failure reason codes.

- [x] **Step 4: Run worker-client tests and verify all cleanup paths**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~UrlRecoveryWorkerClientTests`

Expected: PASS for success, malformed output, timeout, cancellation, and cleanup.

- [ ] **Step 5: Commit the parent process boundary**

```bash
git add src/InvoiceFlowAI.Infrastructure/Url/Worker/IUrlRecoveryWorkerProcessRunner.cs src/InvoiceFlowAI.Infrastructure/Url/Worker/UrlRecoveryWorkerProcessRunner.cs src/InvoiceFlowAI.Infrastructure/Url/Worker/UrlRecoveryWorkerClient.cs src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryWorkerClientTests.cs
git commit -m "feat: run URL recovery in bounded worker"
```

### Task 4: Publish the worker beside the desktop application (Implemented; Automated Publish Test Missing)

**Files:**

- Modify: `src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj`
- Create: `build/windows/publish_dotnet_app.ps1`
- Test: `tests/InvoiceFlowAI.App.Tests/EndToEnd/UrlRecoveryWorkerPublishTests.cs`
- Verify: `src/InvoiceFlowAI.Installer/Product.wxs`

- [ ] **Step 1: Add a publish test that requires the worker executable beside `InvoiceFlowAI.exe`**

```csharp
[Fact]
public void Dotnet_publish_contains_worker_executable_next_to_desktop_app()
{
    var publishRoot = PublishAppToTemporaryDirectory();

    File.Exists(Path.Combine(publishRoot, "InvoiceFlowAI.exe")).Should().BeTrue();
    File.Exists(Path.Combine(publishRoot, "InvoiceFlowAI.UrlRecovery.Worker.exe")).Should().BeTrue();
}
```

- [ ] **Step 2: Run the focused publish test and confirm the sidecar is absent**

Run: `dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter FullyQualifiedName~UrlRecoveryWorkerPublishTests`

Expected: FAIL because the App publish currently contains no worker executable.

- [x] **Step 3: Add the worker project reference and a deterministic publish command**

```xml
<ProjectReference Include="..\InvoiceFlowAI.UrlRecovery.Worker\InvoiceFlowAI.UrlRecovery.Worker.csproj"
                  ReferenceOutputAssembly="false" />
```

The PowerShell script publishes `InvoiceFlowAI.App.csproj` for `win-x64`, copies the worker apphost and runtime files beside the app, then invokes the existing release-manifest scripts. `Product.wxs` already harvests all files under `publish/win-x64`; do not add a second unmanaged installer file list.

- [x] **Step 4: Run Windows publish smoke, release-manifest verification, and App tests**

Run: `dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter FullyQualifiedName~UrlRecoveryWorkerPublishTests`

Expected: PASS; both executables appear in the generated release manifest and in WiX's harvested publish tree.

- [ ] **Step 5: Commit worker packaging**

```bash
git add src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj build/windows/publish_dotnet_app.ps1 tests/InvoiceFlowAI.App.Tests/EndToEnd/UrlRecoveryWorkerPublishTests.cs
git commit -m "build: publish URL recovery worker with desktop app"
```

### Task 5: Connect source-group history to completed artifact processing (Complete)

**Files:**

- Create: `src/InvoiceFlowAI.Application/Persistence/ICandidateSourceWriter.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfCandidateSourceWriter.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfCandidateHistoryReader.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/EfCandidateHistoryReaderTests.cs`

**Interface:** `ICandidateSourceWriter.UpsertSelectedArtifactAsync(DocumentCandidate candidate, string contentSha256, DocumentIdentity sourceGroupIdentity, CancellationToken cancellationToken)` stores the selected artifact source row and its opaque provider-group key. Invoke it when the selected candidate identity is persisted, before downstream extraction; the history reader still requires a completed, non-retryable processing row before skipping a future run.

- [x] **Step 1: Add a SQLite test for an archived artifact whose `ProviderGroupKey` is the source group identity**

```csharp
[Fact]
public async Task Finds_completed_artifact_history_by_source_group_key()
{
    await _fixture.ResetAsync();
    await using (var context = _fixture.CreateContext())
    {
        var source = NewSource("artifact-id");
        source.ProviderGroupKey = "source-group-id";
        context.Documents.Add(source);
        context.Runs.Add(NewRun("run-artifact-id"));
        await context.SaveChangesAsync();
        context.DocumentProcessing.Add(NewRecord(
            "artifact-id", completedAtUtc: DateTimeOffset.UnixEpoch, retryable: false));
        await context.SaveChangesAsync();
    }

    await using var queryContext = _fixture.CreateContext();
    var historyReader = new EfCandidateHistoryReader(queryContext);

    (await historyReader.ExistsAsync(DocumentIdentity.Create("source-group-id"), CancellationToken.None))
        .Should().BeTrue();
}
```

- [x] **Step 2: Run the EF history reader tests and confirm only direct document IDs are queried**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~EfCandidateHistoryReaderTests`

Expected: FAIL on source-group lookup.

- [x] **Step 3: Persist the safe group identity on the selected artifact source row and query completed history through it**

```csharp
var documentIds = _context.Documents.AsNoTracking()
    .Where(document => document.DocumentId == identity.Value || document.ProviderGroupKey == identity.Value)
    .Select(document => document.DocumentId);
return await _context.DocumentProcessing.AsNoTracking()
    .AnyAsync(row => documentIds.Contains(row.DocumentId)
        && row.CompletedAtUtc != null
        && !row.Retryable, cancellationToken);
```

Write `ProviderGroupKey` only as the opaque group identity; do not store source URLs. Upsert the source row when the selected artifact candidate is persisted, before extraction. This is safe because `EfCandidateHistoryReader` returns true only after a completed, non-retryable processing row exists, so interrupted or failed work remains eligible for retry.

- [x] **Step 4: Run EF history tests and full solution tests**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~EfCandidateHistoryReaderTests`

Expected: PASS for completed, retryable, incomplete, and missing group cases.

- [ ] **Step 5: Commit source-group history linking**

```bash
git add src/InvoiceFlowAI.Application/Persistence/ICandidateSourceWriter.cs src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfCandidateSourceWriter.cs src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfCandidateHistoryReader.cs src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/EfCandidateHistoryReaderTests.cs
git commit -m "feat: link completed URL artifacts to source groups"
```

### Task 6: Add recovery pipeline composition coverage (Partial)

**Files:**

- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryPipelineIntegrationTests.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Verify: `src/InvoiceFlowAI.Application/Pipeline/PipelineRecipeGraphFactory.cs`

- [ ] **Step 1: Add a deterministic test from fake mailbox session through candidate collection and worker recovery into a fake extraction/pairing/archive boundary**

Assert that same-message links create one group, worker results select one primary artifact, raw query tokens do not appear in metadata or safe failures, and pre-existing terminal results survive.

- [ ] **Step 2: Run the integration test and confirm it exposes missing production registrations**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~UrlRecoveryPipelineIntegrationTests`

Expected: FAIL if a required production stage or worker client is not registered.

- [x] **Step 3: Wire the worker client and source writer without inventing extraction/archive implementations**

Register the group-aware worker client and source writer in `AddInvoiceFlowInfrastructure`; use test doubles for `IDocumentExtractionStage`, `IArtifactPairingStage`, and `IDocumentArchivingStage` until their concrete implementations exist.

- [x] **Step 4: Run the integration test and full solution suite**

Run: `dotnet test InvoiceFlowAI.sln`

Expected: PASS; full archive persistence remains explicitly outside this test until concrete C# archive stages are available.

- [ ] **Step 5: Commit pipeline composition coverage**

```bash
git add tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryPipelineIntegrationTests.cs src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs
git commit -m "test: cover grouped URL recovery pipeline"
```
