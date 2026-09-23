using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Mail;

public sealed class MailboxChannelRegistryTests
{
    [Fact]
    public void Qq_domain_resolves_without_id_command()
    {
        var registry = new MailboxChannelRegistry();

        var capabilities = registry.Resolve("alice@qq.com");

        capabilities.Domain.Should().Be("qq.com");
        capabilities.DisplayName.Should().Be("QQ Mail");
        capabilities.RequiresIdCommand.Should().BeFalse();
    }

    [Fact]
    public void Mail163_domain_resolves_with_id_command()
    {
        var registry = new MailboxChannelRegistry();

        var capabilities = registry.Resolve("alice@163.com");

        capabilities.Domain.Should().Be("163.com");
        capabilities.DisplayName.Should().Be("163 Mail");
        capabilities.RequiresIdCommand.Should().BeTrue();
    }

    [Fact]
    public void Domain_matching_is_case_insensitive()
    {
        var registry = new MailboxChannelRegistry();

        var capabilities = registry.Resolve("ALICE@Qq.CoM");

        capabilities.Domain.Should().Be("qq.com");
        capabilities.RequiresIdCommand.Should().BeFalse();
    }

    [Fact]
    public void Unknown_domain_returns_non_overriding_capabilities()
    {
        var registry = new MailboxChannelRegistry();

        var capabilities = registry.Resolve("alice@example.com");

        capabilities.Domain.Should().Be("unknown");
        capabilities.DisplayName.Should().Be("Unknown");
        capabilities.RequiresIdCommand.Should().BeFalse();
    }
}