using InvoiceFlowAI.Application.Candidates;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using InvoiceFlowAI.Infrastructure.Url;
using InvoiceFlowAI.Infrastructure.Url.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.Infrastructure;

public static class InvoiceFlowAIInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInvoiceFlowInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ICandidateIdentityKeyProvider, SecretStoreCandidateIdentityKeyProvider>();
        services.AddScoped<ICandidateIdentityFactory, CandidateIdentityFactory>();
        services.AddScoped<ICandidateHistoryReader, EfCandidateHistoryReader>();
        services.AddScoped<ICandidateSourceWriter, EfCandidateSourceWriter>();
        services.AddScoped<ICandidateCollectionStage, CandidateCollectionStage>();
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
        services.AddScoped<PublicUrlPolicy>();
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