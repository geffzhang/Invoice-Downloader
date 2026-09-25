// Verifies ClosedXmlReportExporter (design §7):
//   * Three fixed sheets (Summary, Invoices, Manual Reviews)
//   * Failed candidates appear as invoice rows with DocumentType="Failed"
//   * Number / date / null formats use invariant culture
//   * Repeated export is content-stable so the same data yields the same hash
//   * Different invoice sets produce different content hashes

using ClosedXML.Excel;
using FluentAssertions;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Infrastructure.Reports;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Reports;

public sealed class ClosedXmlReportExporterTests
{
    [Fact]
    public async Task Export_writes_three_sheets_in_fixed_order()
    {
        var tmp = NewTempDir();
        try
        {
            var exporter = new ClosedXmlReportExporter(new NopFileSystem(), path => Path.Combine(tmp, path));
            var workItem = NewWorkItem();

            await exporter.ExportAsync(workItem, CancellationToken.None);

            using var workbook = new XLWorkbook(Path.Combine(tmp, workItem.RelativePath));
            var sheets = workbook.Worksheets.ToList();
            sheets.Count.Should().Be(3);
            sheets[0].Name.Should().Be("Summary");
            sheets[1].Name.Should().Be("Invoices");
            sheets[2].Name.Should().Be("Manual Reviews");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Failed_candidates_appear_as_invoice_rows()
    {
        var tmp = NewTempDir();
        try
        {
            var exporter = new ClosedXmlReportExporter(new NopFileSystem(), path => Path.Combine(tmp, path));
            var workItem = NewWorkItem() with
            {
                Invoices = new List<ReportInvoiceRow>
                {
                    NewInvoice("inv-1", "FlightInvoice"),
                    NewInvoice("inv-2", "Failed"),
                }.AsReadOnly(),
            };

            await exporter.ExportAsync(workItem, CancellationToken.None);

            using var workbook = new XLWorkbook(Path.Combine(tmp, workItem.RelativePath));
            var sheet = workbook.Worksheet("Invoices");
            sheet.Cell(2, 10).GetString().Should().Be("FlightInvoice");
            sheet.Cell(3, 10).GetString().Should().Be("Failed");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Numbers_and_dates_use_invariant_culture()
    {
        var tmp = NewTempDir();
        try
        {
            var exporter = new ClosedXmlReportExporter(new NopFileSystem(), path => Path.Combine(tmp, path));
            var workItem = NewWorkItem();

            await exporter.ExportAsync(workItem, CancellationToken.None);

            using var workbook = new XLWorkbook(Path.Combine(tmp, workItem.RelativePath));
            var sheet = workbook.Worksheet("Invoices");
            sheet.Cell(2, 2).GetString().Should().Be("2026-09-15");
            sheet.Cell(2, 5).GetString().Should().Be("100.00");
            sheet.Cell(2, 6).GetString().Should().Be("13.00");
            sheet.Cell(2, 7).GetString().Should().Be("113.00");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Same_data_yields_same_content_hash()
    {
        var tmp = NewTempDir();
        try
        {
            var exporter = new ClosedXmlReportExporter(new NopFileSystem(), path => Path.Combine(tmp, path));
            var first = await exporter.ExportAsync(NewWorkItem(), CancellationToken.None);
            var second = await exporter.ExportAsync(NewWorkItem(), CancellationToken.None);

            first.ContentHash.Should().Be(second.ContentHash);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Different_invoice_data_yields_different_content_hash()
    {
        var tmp = NewTempDir();
        try
        {
            var exporter = new ClosedXmlReportExporter(new NopFileSystem(), path => Path.Combine(tmp, path));
            var first = await exporter.ExportAsync(NewWorkItem(), CancellationToken.None);
            var mutated = NewWorkItem() with
            {
                Invoices = new List<ReportInvoiceRow> { NewInvoice("inv-different", "FlightInvoice") }.AsReadOnly(),
            };
            var second = await exporter.ExportAsync(mutated, CancellationToken.None);

            first.ContentHash.Should().NotBe(second.ContentHash);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task AlreadyExisted_true_when_file_present()
    {
        var tmp = NewTempDir();
        try
        {
            var exporter = new ClosedXmlReportExporter(new NopFileSystem(), path => Path.Combine(tmp, path));
            var workItem = NewWorkItem();

            await exporter.ExportAsync(workItem, CancellationToken.None);
            var second = await exporter.ExportAsync(workItem, CancellationToken.None);

            second.AlreadyExisted.Should().BeTrue();
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Summary_sheet_writes_run_metadata()
    {
        var tmp = NewTempDir();
        try
        {
            var exporter = new ClosedXmlReportExporter(new NopFileSystem(), path => Path.Combine(tmp, path));

            await exporter.ExportAsync(NewWorkItem(), CancellationToken.None);

            using var workbook = new XLWorkbook(Path.Combine(tmp, "reports/run-1/report.xlsx"));
            var summary = workbook.Worksheet("Summary");
            summary.Cell("A1").GetString().Should().Be("RunId");
            summary.Cell("B1").GetString().Should().Be("run-1");
            summary.Cell("A2").GetString().Should().Be("Status");
            summary.Cell("B2").GetString().Should().Be("Completed");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Summary_sheet_writes_all_candidate_counts_and_failure_reasons()
    {
        var tmp = NewTempDir();
        try
        {
            var workItem = NewWorkItem() with
            {
                Summary = NewWorkItem().Summary with
                {
                    ResolvedCount = 2,
                    DuplicateCount = 3,
                    RetainedCount = 4,
                    ManualReviewCount = 5,
                    UnresolvedCount = 6,
                    CancelledCount = 7,
                    QuotaExhaustedCount = 8,
                    AuthFailedCount = 9,
                    TimeoutCount = 10,
                },
                Failures = new List<ReportFailureRow> { new ReportFailureRow("AUTH_FAILED", 9) },
            };
            var exporter = new ClosedXmlReportExporter(new NopFileSystem(), path => Path.Combine(tmp, path));

            await exporter.ExportAsync(workItem, CancellationToken.None);

            using var workbook = new XLWorkbook(Path.Combine(tmp, workItem.RelativePath));
            var summary = workbook.Worksheet("Summary");
            summary.Cell("B5").GetString().Should().Be("2");
            summary.Cell("B8").GetString().Should().Be("3");
            summary.Cell("B9").GetString().Should().Be("4");
            summary.Cell("B6").GetString().Should().Be("5");
            summary.Cell("B10").GetString().Should().Be("6");
            summary.Cell("B11").GetString().Should().Be("7");
            summary.Cell("B12").GetString().Should().Be("8");
            summary.Cell("B13").GetString().Should().Be("9");
            summary.Cell("B14").GetString().Should().Be("10");
            summary.Cell("A17").GetString().Should().Be("AUTH_FAILED");
            summary.Cell("B17").GetString().Should().Be("9");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Output_root_is_used_and_parent_traversal_is_rejected()
    {
        var tmp = NewTempDir();
        try
        {
            var exporter = new ClosedXmlReportExporter(new NopFileSystem(), _ => throw new InvalidOperationException());
            var workItem = NewWorkItem() with { OutputRoot = tmp };

            await exporter.ExportAsync(workItem, CancellationToken.None);
            File.Exists(Path.Combine(tmp, workItem.RelativePath)).Should().BeTrue();

            var traversal = workItem with { RelativePath = "../outside.xlsx" };
            var act = () => exporter.ExportAsync(traversal, CancellationToken.None);
            await act.Should().ThrowAsync<ArgumentException>();
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), $"invoiceflow-report-{Guid.NewGuid():N}");

    private static ReportExportWorkItem NewWorkItem() => new(
        RunId: "run-1",
        RelativePath: "reports/run-1/report.xlsx",
        Invoices: new List<ReportInvoiceRow> { NewInvoice("inv-1", "FlightInvoice") }.AsReadOnly(),
        ManualReviews: new List<ReportManualReviewRow>().AsReadOnly(),
        Failures: new List<ReportFailureRow>().AsReadOnly(),
        Summary: new ReportSummaryRow(
            RunId: "run-1",
            Status: "Completed",
            ReasonCode: "RUN_COMPLETED",
            ResolvedCount: 1,
            DuplicateCount: 0,
            RetainedCount: 0,
            ManualReviewCount: 0,
            UnresolvedCount: 0,
            CancelledCount: 0,
            QuotaExhaustedCount: 0,
            AuthFailedCount: 0,
            TimeoutCount: 0,
            CompletedAtUtc: new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)),
        TemplateVersion: "1.0.0");

    private static ReportInvoiceRow NewInvoice(string id, string docType) => new(
        InvoiceId: id,
        InvoiceDate: new DateOnly(2026, 9, 15),
        Purchaser: "ACME Holdings",
        Seller: "Air Berlin",
        Amount: 100m,
        TaxAmount: 13m,
        TotalAmount: 113m,
        InvoiceNumber: "INV-001",
        InvoiceCode: "CODE-001",
        DocumentType: docType,
        Category: "transport",
        Confidence: "0.95",
        ArchiveState: "Committed");

    private sealed class NopFileSystem : InvoiceFlowAI.Application.Archive.IArchiveFileSystem
    {
        public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(File.Exists(path));
        public Task<string> CopyToSiblingTempAsync(string sourcePath, string finalFilePath, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task AtomicMoveAsync(string sourcePath, string targetPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FlushToDiskAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}