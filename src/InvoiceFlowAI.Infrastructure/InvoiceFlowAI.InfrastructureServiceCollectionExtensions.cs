using System.Net;
using InvoiceFlowAI.Application.Candidates;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Security;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Archive;
using InvoiceFlowAI.Infrastructure.Ai;
using InvoiceFlowAI.Infrastructure.Ocr;
using InvoiceFlowAI.Infrastructure.Parsers;
using InvoiceFlowAI.Infrastructure.Pipeline;
using InvoiceFlowAI.Infrastructure.Url;
using InvoiceFlowAI.Infrastructure.Url.Worker;
using InvoiceFlowAI.Infrastructure.Security;
using InvoiceFlowAI.Infrastructure.Mail;
using InvoiceFlowAI.Infrastructure.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceFlowAI.Infrastructure;

public static class InvoiceFlowAIInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInvoiceFlowInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISessionSecretStore, SessionSecretStore>();
        services.TryAddSingleton<ISecretRetentionStore, SecretApplicationService>();
        services.TryAddSingleton<ISecretStore>(serviceProvider => new CompositeSecretStore(
            serviceProvider.GetRequiredService<IPersistentSecretStore>(),
            serviceProvider.GetRequiredService<ISessionSecretStore>()));
        services.AddScoped<AccountTestService>();
        services.AddScoped<AccountApplicationService>();
        services.AddScoped<IMailboxConnectionTester, MailKitMailboxConnectionTester>();
        services.AddScoped<IProviderConnectionTester, DeepSeekProviderConnectionTester>();
        services.TryAddSingleton<IMailboxSessionFactory, MailboxSessionFactory>();
        services.AddScoped<IMailboxScanner, MailKitMailboxScanner>();
        services.AddScoped<IChatCompletionService>(serviceProvider =>
            new DeepSeekChatCompletionService(serviceProvider.GetRequiredService<ISecretStore>()));
        services.AddScoped<InvoiceNormalizer>();
        services.AddScoped<IInvoiceAcceptanceService, InvoiceAcceptanceService>();
        services.AddScoped<AiAuthenticationFailureGate>();
        services.AddScoped<IInvoiceFieldExtractor, InvoiceFieldExtractor>();
        services.AddScoped<ICandidateIdentityKeyProvider, SecretStoreCandidateIdentityKeyProvider>();
        services.AddScoped<ICandidateIdentityFactory, CandidateIdentityFactory>();
        services.AddScoped<ICandidateHistoryReader, EfCandidateHistoryReader>();
        services.AddScoped<ICandidateSourceWriter, EfCandidateSourceWriter>();
        services.AddScoped<IPairingStore, EfPairingStore>();
        services.AddScoped<IManualReviewItemStore, EfManualReviewItemStore>();
        services.AddScoped<IAuditEventStore, EfAuditStore>();
        services.AddScoped<IRunLifecycleStore, EfRunLifecycleStore>();
        services.AddScoped<IRunCheckpointStore, EfRunCheckpointStore>();
        services.AddScoped<IEventReplayStore, EfEventReplayStore>();
        services.AddScoped<IUnitOfWorkFactory, EfUnitOfWorkFactory>();
        services.AddScoped<EfReportRunDataStore>();
        services.AddScoped<IReportRunDataSource>(provider => provider.GetRequiredService<EfReportRunDataStore>());
        services.AddScoped<IReportPathStore>(provider => provider.GetRequiredService<EfReportRunDataStore>());
        services.TryAddSingleton<IReportOpenTokenStore, InMemoryReportOpenTokenStore>();
        services.TryAddScoped<IReportExporter>(provider => new ClosedXmlReportExporter(
            provider.GetRequiredService<IArchiveFileSystem>(),
            relativePath => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InvoiceFlowAI",
                relativePath)));
        services.AddScoped<IReportApplicationService, ReportApplicationService>();
        services.AddScoped<IArchiveArtifactStore, EfArchiveArtifactStore>();
        services.AddScoped<ILegacyArchiveInventoryStore, EfLegacyArchiveInventoryStore>();
        services.AddScoped<ICwtArchiveInventory, CwtArchiveInventory>();
        services.AddSingleton<IArchiveFileSystem, PhysicalArchiveFileSystem>();
        services.AddSingleton<IArchiveNamingPolicy, ArchiveNamingPolicy>();
        services.AddScoped<IArchiveCommitCoordinator, ArchiveCommitCoordinator>();
        services.AddScoped<IArchiveRecoveryService, ArchiveRecoveryService>();
        services.AddScoped<ICwtCancellationFinalizer, CwtCancellationFinalizer>();
        services.AddScoped<IDocumentArchivingStage, DocumentArchivingStage>();
        services.AddScoped<ICandidateCollectionStage, CandidateCollectionStage>();
        services.AddScoped<IParser, XmlInvoiceParser>();
        services.AddScoped<IParser, OfdInvoiceParser>();
        services.AddScoped<IParser, PdfInvoiceParser>();
        services.AddScoped<IParser, RideItineraryParser>();
        services.AddScoped<IParser, DidiInvoiceParser>();
        services.AddScoped<IParser, RailwayTicketParser>();
        services.AddScoped<IParser, AccommodationFolioParser>();
        services.AddScoped<IParser, CitsGbtParser>();
        services.AddScoped<IParser, ForeignInvoiceParser>();
        services.AddScoped<IParserRegistry>(serviceProvider => new ParserRegistry(serviceProvider.GetServices<IParser>()));
        services.AddScoped<IDocumentExtractionStage>(serviceProvider => new DocumentExtractionStage(
            serviceProvider.GetServices<IParser>(),
            serviceProvider.GetRequiredService<IInvoiceFieldExtractor>(),
            serviceProvider.GetRequiredService<InvoiceNormalizer>(),
            serviceProvider.GetRequiredService<IInvoiceAcceptanceService>(),
            serviceProvider.GetRequiredService<InvoiceExtractionRules>()));
        services.AddSingleton<IPdfPageRenderer, PdfiumPageRenderer>();
        services.AddSingleton<IOcrFallback, SimdPaddleOcrFallback>();
        services.AddUrlRecoveryStrategies();
        services.AddScoped<UrlRecoveryWorkerManifestStore>();
        services.AddScoped<IUrlRecoveryWorkerProcessRunner, UrlRecoveryWorkerProcessRunner>();
        services.AddScoped<IUrlRecoveryClient, UrlRecoveryWorkerClient>();
        services.AddScoped<IUrlRecoveryStage, UrlRecoveryStage>();
        return services;
    }

    public static IServiceCollection AddUrlRecoveryStrategies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped(serviceProvider => new PublicUrlPolicy(proxy: HttpClient.DefaultProxy));
        services.AddScoped<IUrlRecoveryTransport, PinnedHttpUrlRecoveryTransport>();
        services.AddScoped<PublicUrlRecoveryClient>();
        services.AddScoped<IUrlRecoveryStrategy, GenericUrlRecoveryStrategy>();
        services.AddScoped<IUrlRecoveryStrategy, NuonuoScanRecoveryStrategy>();
        services.AddScoped<IUrlRecoveryStrategy, DirectInvoiceRecoveryStrategy>();
        services.AddScoped<IUrlRecoveryStrategy, BaiwangRecoveryStrategy>();
        services.AddScoped<DirectArtifactProbe>();
        services.AddScoped<GenericUrlRecoveryStrategy>();
        services.AddScoped(serviceProvider => new UrlRecoveryStrategyRegistry(
            serviceProvider.GetServices<IUrlRecoveryStrategy>(),
            serviceProvider.GetRequiredService<GenericUrlRecoveryStrategy>()));
        services.AddScoped<IUrlRecoveryClient>(serviceProvider => serviceProvider.GetRequiredService<UrlRecoveryStrategyRegistry>());
        return services;
    }
}