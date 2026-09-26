using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Url;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class UrlRecoveryStrategyRegistryTests
{
    [Fact]
    public async Task Dispatches_known_provider_family_to_exactly_one_strategy()
    {
        var nuonuo = new FakeStrategy("nuonuo_scan_invoice");
        var fallback = new FakeStrategy();
        var registry = new UrlRecoveryStrategyRegistry([nuonuo], fallback);

        var result = await registry.RecoverAsync(Group("nuonuo_scan_invoice"), CancellationToken.None);

        result.Should().BeSameAs(nuonuo.Result);
        nuonuo.CallCount.Should().Be(1);
        fallback.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_family_uses_bounded_generic_fallback()
    {
        var known = new FakeStrategy("baiwang");
        var fallback = new FakeStrategy();
        var registry = new UrlRecoveryStrategyRegistry([known], fallback);

        await registry.RecoverAsync(Group("unclassified"), CancellationToken.None);

        known.CallCount.Should().Be(0);
        fallback.CallCount.Should().Be(1);
    }

    [Fact]
    public void Rejects_duplicate_handlers_for_one_provider_family()
    {
        var first = new FakeStrategy("baiwang");
        var second = new FakeStrategy("baiwang");

        var act = () => new UrlRecoveryStrategyRegistry([first, second], new FakeStrategy());

        act.Should().Throw<ArgumentException>()
            .WithMessage("*baiwang*");
    }

    private static UrlCandidateGroup Group(string providerFamily)
    {
        var candidate = new MailboxUrlCandidate(
            "account", "INBOX", "validity", "uid", new Uri("https://fixture.invalid/invoice"),
            providerFamily, "opaque", new Dictionary<string, string>(), 0);
        return new UrlCandidateGroup(providerFamily, [candidate], new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create("group-id"));
    }

    private sealed class FakeStrategy(params string[] providerFamilies) : IUrlRecoveryStrategy
    {
        public IReadOnlyCollection<string> ProviderFamilies { get; } = providerFamilies;
        public int CallCount { get; private set; }
        public UrlRecoveryResult Result { get; } = new(ReadOnlyMemory<byte>.Empty, "application/pdf");

        public Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(Result);
        }
    }
}