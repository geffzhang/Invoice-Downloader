using FluentAssertions;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Pipeline;

public sealed class CandidateBatchContractTests
{
    [Fact]
    public void Candidate_batch_defaults_terminal_results_and_accepts_explicit_terminal_results()
    {
        var emptyBatch = new CandidateBatch(Array.Empty<CandidateWorkItem>());
        var terminal = CreateTerminalResult("retained-candidate", CandidateStatus.Retained);
        var batch = new CandidateBatch(Array.Empty<CandidateWorkItem>(), new[] { terminal });

        emptyBatch.EffectiveTerminalResults.Should().BeEmpty();
        batch.EffectiveTerminalResults.Should().ContainSingle().Which.Should().BeSameAs(terminal);
    }

    [Fact]
    public void Extraction_batch_defaults_preflight_results_and_preserves_legacy_constructor()
    {
        var result = CreateTerminalResult("manual-candidate", CandidateStatus.ManualReview);
        var batch = new ExtractionBatch(Array.Empty<CandidateProcessResult>())
        {
            PreflightResults = new[] { result },
        };

        batch.Results.Should().BeEmpty();
        batch.EffectivePreflightResults.Should().ContainSingle().Which.Should().BeSameAs(result);
        new ExtractionBatch(Array.Empty<CandidateProcessResult>()).EffectivePreflightResults.Should().BeEmpty();
    }

    private static CandidateProcessResult CreateTerminalResult(string id, CandidateStatus status)
    {
        var candidate = new DocumentCandidate(
            DocumentIdentity.Create(id),
            0,
            $"correlation-{id}",
            "message-uid",
            $"{id}.pdf",
            "application/pdf",
            1,
            0,
            "mime_attachment");
        var failure = status == CandidateStatus.Retained
            ? null
            : new CandidateFailure(
                "CANDIDATE_PREFLIGHT_TERMINAL",
                FailureScope.Candidate,
                FailureCategory.Validation,
                false,
                "Candidate requires review.");
        return new CandidateProcessResult(candidate, status, Failure: failure);
    }
}