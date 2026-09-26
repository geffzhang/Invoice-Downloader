using FluentAssertions;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Candidates;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Security;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Infrastructure;
using InvoiceFlowAI.Infrastructure.Ai;
using InvoiceFlowAI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Pipeline;

public sealed class ExtractionServiceRegistrationTests
{
    [Fact]
    public void Infrastructure_registers_one_composite_secret_store_and_retention_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPersistentSecretStore>(new FakePersistentSecretStore());
        services.AddInvoiceFlowInfrastructure();

        services.Count(x => x.ServiceType == typeof(ISecretStore)).Should().Be(1);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISecretStore>().Should().BeOfType<CompositeSecretStore>();
        provider.GetRequiredService<ISecretRetentionStore>().Should().BeOfType<SecretApplicationService>();
    }

    [Fact]
    public async Task Infrastructure_registers_scoped_extraction_services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(new FakeSecretStore());
        services.AddSingleton(new InvoiceExtractionRules("Example Company"));
        services.AddSingleton<IUrlRecoveryClient>(new FakeUrlRecoveryClient());
        services.AddSingleton<ICandidateIdentityFactory>(new FakeCandidateIdentityFactory());
        services.AddSingleton<ICandidateHistoryReader>(new FakeCandidateHistoryReader());
        services.AddInvoiceFlowInfrastructure();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDocumentExtractionStage>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IInvoiceFieldExtractor>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<AiAuthenticationFailureGate>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IChatCompletionService>().Should().BeOfType<DeepSeekChatCompletionService>();
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task SaveAsync(string name, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult<string?>("test-key");
        public Task DeleteAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakePersistentSecretStore : IPersistentSecretStore
    {
        public Task SaveAsync(string name, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeUrlRecoveryClient : IUrlRecoveryClient
    {
        public Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCandidateIdentityFactory : ICandidateIdentityFactory
    {
        public Task<InvoiceFlowAI.Domain.Candidates.DocumentIdentity> CreateAttachmentAsync(string accountId, string mailbox, string uidValidity, InvoiceFlowAI.Application.Mail.MailboxAttachmentCandidate attachment, CancellationToken cancellationToken) =>
            Task.FromResult(InvoiceFlowAI.Domain.Candidates.DocumentIdentity.Create("attachment"));
        public Task<InvoiceFlowAI.Domain.Candidates.DocumentIdentity> CreateUrlGroupAsync(string providerFamily, IReadOnlyList<InvoiceFlowAI.Application.Mail.MailboxUrlCandidate> candidates, CancellationToken cancellationToken) =>
            Task.FromResult(InvoiceFlowAI.Domain.Candidates.DocumentIdentity.Create("url"));
        public Task<InvoiceFlowAI.Domain.Candidates.DocumentIdentity> CreateUrlAsync(InvoiceFlowAI.Application.Mail.MailboxUrlCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(InvoiceFlowAI.Domain.Candidates.DocumentIdentity.Create("url"));
        public Task<InvoiceFlowAI.Domain.Candidates.DocumentIdentity> CreateRecoveredArtifactAsync(InvoiceFlowAI.Domain.Candidates.DocumentIdentity groupIdentity, InvoiceFlowAI.Application.Url.RecoveredArtifactKind kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            Task.FromResult(groupIdentity);
    }

    private sealed class FakeCandidateHistoryReader : ICandidateHistoryReader
    {
        public Task<bool> ExistsAsync(InvoiceFlowAI.Domain.Candidates.DocumentIdentity identity, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
