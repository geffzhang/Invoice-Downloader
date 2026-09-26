using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Application.Candidates;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using System.Security.Cryptography;

namespace InvoiceFlowAI.Application.Pipeline;

public sealed class UrlRecoveryStage : IUrlRecoveryStage
{
    private const int MaxConcurrentRecoveryGroups = 10;
    private const int MaxConcurrentProviderGroups = 4;

    private readonly IUrlRecoveryClient _recoveryClient;
    private readonly ICandidateIdentityFactory? _identityFactory;
    private readonly ICandidateSourceWriter? _sourceWriter;

    public UrlRecoveryStage(IUrlRecoveryClient recoveryClient)
        => _recoveryClient = recoveryClient ?? throw new ArgumentNullException(nameof(recoveryClient));

    public UrlRecoveryStage(IUrlRecoveryClient recoveryClient, ICandidateIdentityFactory identityFactory)
    {
        _recoveryClient = recoveryClient ?? throw new ArgumentNullException(nameof(recoveryClient));
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
    }

    public UrlRecoveryStage(
        IUrlRecoveryClient recoveryClient,
        ICandidateIdentityFactory identityFactory,
        ICandidateSourceWriter sourceWriter)
    {
        _recoveryClient = recoveryClient ?? throw new ArgumentNullException(nameof(recoveryClient));
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _sourceWriter = sourceWriter ?? throw new ArgumentNullException(nameof(sourceWriter));
    }

    public async Task<CandidateBatch> ExecuteAsync(CandidateBatch input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var groupConcurrency = new SemaphoreSlim(MaxConcurrentRecoveryGroups);
        using var providerConcurrency = new SemaphoreSlim(MaxConcurrentProviderGroups);
        var recoveryTasks = input.Items
            .Select(item => RecoverAsync(item, groupConcurrency, providerConcurrency, cancellationToken))
            .ToArray();
        var recoveryAttempts = await Task.WhenAll(recoveryTasks).ConfigureAwait(false);

        var recoveredItems = new List<CandidateWorkItem>(input.Items.Count);
        var terminalResults = input.EffectiveTerminalResults.ToList();

        for (var index = 0; index < input.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = input.Items[index];
            var attempt = recoveryAttempts[index];
            if (attempt is null)
            {
                recoveredItems.Add(item);
                continue;
            }

            try
            {
                if (attempt.Failure is { } failure)
                {
                    terminalResults.Add(ToTerminalResult(item.Candidate, failure));
                    continue;
                }

                var result = attempt.Result!;
                var selected = result.SelectedArtifact;
                if (selected is null)
                {
                    throw new UrlRecoveryException(
                        "URL_RECOVERY_NO_SELECTED_ARTIFACT",
                        "Invoice link did not yield a selectable document.",
                        false,
                        false);
                }

                var identity = item.SourceUrlGroup is { } group
                    ? await (_identityFactory ?? throw new InvalidOperationException("Grouped URL recovery requires an identity factory."))
                        .CreateRecoveredArtifactAsync(group.GroupIdentity, selected.Kind, selected.Content, cancellationToken)
                        .ConfigureAwait(false)
                    : item.Candidate.DocumentId;
                var candidate = item.Candidate with
                {
                    DocumentId = identity,
                    OriginalFileName = WithRecoveredExtension(item.Candidate.OriginalFileName, selected.Kind),
                    ContentType = string.IsNullOrWhiteSpace(selected.ContentType) ? item.Candidate.ContentType : selected.ContentType,
                    ContentLength = selected.Content.Length,
                };
                if (_sourceWriter is not null)
                {
                    var sourceGroupIdentity = item.SourceUrlGroup?.GroupIdentity ?? item.Candidate.DocumentId;
                    var contentSha256 = Convert.ToHexString(SHA256.HashData(selected.Content.Span)).ToLowerInvariant();
                    await _sourceWriter.UpsertSelectedArtifactAsync(
                        candidate,
                        contentSha256,
                        sourceGroupIdentity,
                        cancellationToken).ConfigureAwait(false);
                }
                recoveredItems.Add(item with { Candidate = candidate, Content = selected.Content });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UrlRecoveryException ex)
            {
                terminalResults.Add(ToTerminalResult(item.Candidate, ex));
            }
            catch (Exception)
            {
                terminalResults.Add(ToTerminalResult(
                    item.Candidate,
                    new UrlRecoveryException("URL_RECOVERY_WORKER_FAILED", "Invoice link could not be recovered.", true, false)));
            }
        }

        return new CandidateBatch(recoveredItems, terminalResults);
    }

    private async Task<UrlRecoveryAttempt?> RecoverAsync(
        CandidateWorkItem item,
        SemaphoreSlim groupConcurrency,
        SemaphoreSlim providerConcurrency,
        CancellationToken cancellationToken)
    {
        var providerFamily = item.SourceUrlGroup?.ProviderFamily ?? item.SourceUrlCandidate?.ProviderFamily;
        if (item.SourceUrlGroup is null && item.SourceUrlCandidate is null)
        {
            return null;
        }

        var isProviderGroup = !string.IsNullOrWhiteSpace(providerFamily);
        var providerSlotAcquired = false;
        var groupSlotAcquired = false;
        try
        {
            if (isProviderGroup)
            {
                await providerConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                providerSlotAcquired = true;
            }

            await groupConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            groupSlotAcquired = true;

            var result = item.SourceUrlGroup is { } sourceGroup
                ? await _recoveryClient.RecoverAsync(sourceGroup, cancellationToken).ConfigureAwait(false)
                : await _recoveryClient.RecoverAsync(item.SourceUrlCandidate!, cancellationToken).ConfigureAwait(false);
            return new UrlRecoveryAttempt(result, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UrlRecoveryException exception)
        {
            return new UrlRecoveryAttempt(null, exception);
        }
        catch (Exception)
        {
            return new UrlRecoveryAttempt(null,
                new UrlRecoveryException("URL_RECOVERY_WORKER_FAILED", "Invoice link could not be recovered.", true, false));
        }
        finally
        {
            if (groupSlotAcquired)
            {
                groupConcurrency.Release();
            }
            if (providerSlotAcquired)
            {
                providerConcurrency.Release();
            }
        }
    }

    private static string WithRecoveredExtension(string fileName, RecoveredArtifactKind kind)
    {
        var extension = kind switch
        {
            RecoveredArtifactKind.Pdf => ".pdf",
            RecoveredArtifactKind.Xml => ".xml",
            RecoveredArtifactKind.Ofd => ".ofd",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return Path.ChangeExtension(fileName, extension);
    }

    private static CandidateProcessResult ToTerminalResult(DocumentCandidate candidate, UrlRecoveryException exception)
    {
        var status = exception.IsTimeout ? CandidateStatus.Timeout : CandidateStatus.Unresolved;
        return new CandidateProcessResult(
            candidate,
            status,
            Failure: new CandidateFailure(
                exception.ReasonCode,
                FailureScope.Candidate,
                FailureCategory.Network,
                exception.Retryable,
                exception.SafeMessage));
    }

    private sealed record UrlRecoveryAttempt(UrlRecoveryResult? Result, UrlRecoveryException? Failure);
}