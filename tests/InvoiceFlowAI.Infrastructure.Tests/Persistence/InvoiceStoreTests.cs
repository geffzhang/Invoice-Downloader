using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class InvoiceStoreTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public InvoiceStoreTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upsert_persists_invoice_and_items_and_replaces_items_on_reupsert()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.Runs.Add(new RunRow
        {
            RunId = "run-invoice",
            State = "Running",
            Stage = "archive-documents",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var uowFactory = new EfUnitOfWorkFactory(context);
        var processingStore = new EfDocumentProcessingStore(context);
        var invoiceStore = new EfInvoiceStore(context);
        var now = DateTimeOffset.UnixEpoch;

        await using (var transaction = await uowFactory.BeginAsync(TransactionPurpose.CandidateCommit, CancellationToken.None))
        {
            await processingStore.UpsertAsync(
                new DocumentProcessingWrite(
                    DocumentId: "doc-invoice",
                    ProcessingRevision: 0,
                    RunId: "run-invoice",
                    SourceKind: "attachment",
                    SourceMessageUid: "uid-1",
                    SourceFileName: "invoice.pdf",
                    Stage: "archive-documents",
                    Status: CandidateStatus.Resolved,
                    ReasonCode: string.Empty,
                    Retryable: false,
                    Attempt: 1,
                    MaxAttempts: 1,
                    ArtifactPath: "/tmp/source.pdf",
                    ResultJson: "{}",
                    TraceJson: null,
                    StartedAtUtc: now,
                    CompletedAtUtc: now),
                transaction,
                CancellationToken.None);
            await invoiceStore.UpsertAsync(NewInvoice(2), 0, transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await using (var transaction = await uowFactory.BeginAsync(TransactionPurpose.CandidateCommit, CancellationToken.None))
        {
            await invoiceStore.UpsertAsync(NewInvoice(1), 0, transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var invoice = await context.Invoices.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        invoice.InvoiceId.Should().Be("doc-invoice");
        invoice.DocumentId.Should().Be("doc-invoice");
        invoice.ProcessingRevision.Should().Be(0);
        invoice.Purchaser.Should().Be("Buyer");
        invoice.Seller.Should().Be("Merchant");
        invoice.TotalAmount.Should().Be("113.00");
        invoice.DocumentType.Should().Be(nameof(InvoiceDocumentType.RideInvoice));
        invoice.ArchiveState.Should().Be("Pending");

        var items = await context.InvoiceItems.AsNoTracking().OrderBy(row => row.Ordinal)
            .ToListAsync(TestContext.Current.CancellationToken);
        items.Should().HaveCount(1);
        items[0].Ordinal.Should().Be(0);
        items[0].Name.Should().Be("Item 0");
        items[0].Amount.Should().Be("56.50");
    }

    private static InvoiceDocument NewInvoice(int itemCount) => new(
        DocumentId: "doc-invoice",
        InvoiceDate: new DateOnly(2026, 9, 24),
        Purchaser: "Buyer",
        Seller: "Merchant",
        Amount: 100m,
        TaxAmount: 13m,
        TotalAmount: 113.00m,
        InvoiceCode: "code",
        InvoiceNumber: "number",
        DocumentType: InvoiceDocumentType.RideInvoice,
        Category: null,
        Route: null,
        Items: Enumerable.Range(0, itemCount)
            .Select(index => new InvoiceItem($"Item {index}", 1m, 56.50m, 56.50m))
            .ToArray(),
        SourceFileName: "invoice.pdf",
        ContentHash: new string('a', 64));
}
