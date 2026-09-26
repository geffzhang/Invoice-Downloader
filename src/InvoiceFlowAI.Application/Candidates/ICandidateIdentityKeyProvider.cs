namespace InvoiceFlowAI.Application.Candidates;

public sealed record CandidateIdentityKey(string Version, ReadOnlyMemory<byte> KeyBytes);

public interface ICandidateIdentityKeyProvider
{
    Task<CandidateIdentityKey> GetCurrentKeyAsync(CancellationToken cancellationToken);
}