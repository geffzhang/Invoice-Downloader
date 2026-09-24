using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url;

public interface IUrlRecoveryStrategy
{
    IReadOnlyCollection<string> ProviderFamilies { get; }

    Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken);
}