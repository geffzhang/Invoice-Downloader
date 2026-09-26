using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Infrastructure;
using InvoiceFlowAI.Infrastructure.Archive;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class PairingStoreRegistrationTests
{
    [Fact]
    public async Task Infrastructure_registers_pairing_and_manual_review_stores()
    {
        var services = new ServiceCollection();
        services.AddDbContext<InvoiceFlowDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddInvoiceFlowInfrastructure();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPairingStore>().Should().BeOfType<EfPairingStore>();
        scope.ServiceProvider.GetRequiredService<IManualReviewItemStore>().Should().BeOfType<EfManualReviewItemStore>();
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>().Should().BeOfType<EfUnitOfWorkFactory>();
        scope.ServiceProvider.GetRequiredService<IArchiveArtifactStore>().Should().BeOfType<EfArchiveArtifactStore>();
        scope.ServiceProvider.GetRequiredService<IArchiveCommitCoordinator>().Should().BeOfType<ArchiveCommitCoordinator>();
        scope.ServiceProvider.GetRequiredService<IArchiveRecoveryService>().Should().BeOfType<ArchiveRecoveryService>();
        scope.ServiceProvider.GetRequiredService<ICwtCancellationFinalizer>().Should().BeOfType<CwtCancellationFinalizer>();
        scope.ServiceProvider.GetRequiredService<IArchiveFileSystem>().Should().BeOfType<PhysicalArchiveFileSystem>();
        scope.ServiceProvider.GetRequiredService<IArchiveNamingPolicy>().Should().BeOfType<ArchiveNamingPolicy>();
        scope.ServiceProvider.GetRequiredService<IDocumentArchivingStage>().Should().BeOfType<DocumentArchivingStage>();
    }
}
