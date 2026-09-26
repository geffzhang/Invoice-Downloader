using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Infrastructure.Mail;
using MailKit.Security;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Mail;

public sealed class MailKitMailboxConnectionTesterTests
{
    [Fact]
    public async Task Test_connects_authenticates_opens_mailbox_and_disconnects()
    {
        var session = new FakeMailboxSession();
        var tester = new MailKitMailboxConnectionTester(new FakeMailboxSessionFactory(session));
        var settings = NewSettings();

        var result = await tester.TestAsync(settings, "private-auth-code", "Invoices", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.AccountId.Should().Be("acct-1");
        result.Mailbox.Should().Be("Invoices");
        result.SafeMessage.Should().NotContain("private-auth-code");
        session.Operations.Should().Equal("connect", "authenticate", "open:Invoices", "disconnect");
        session.LastCredential.Should().Be("private-auth-code");
    }

    [Fact]
    public async Task Authentication_failure_returns_stable_safe_result_and_disconnects()
    {
        var session = new FakeMailboxSession
        {
            AuthenticateFailure = new MailKit.Security.AuthenticationException("private-auth-code rejected"),
        };
        var tester = new MailKitMailboxConnectionTester(new FakeMailboxSessionFactory(session));

        var result = await tester.TestAsync(NewSettings(), "private-auth-code", null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureCode.Should().Be("IMAP_LOGIN_FAILED");
        result.SafeMessage.Should().NotContain("private-auth-code");
        result.SafeMessage.Should().NotContain("rejected");
        session.Operations.Should().Contain("disconnect");
    }

    private static MailboxConnectionSettings NewSettings()
        => new("acct-1", "alice@example.com", "imap.example.com", 993, true, "mail.imap.auth-code", "INBOX");

    private sealed class FakeMailboxSessionFactory(FakeMailboxSession session) : IMailboxSessionFactory
    {
        public IMailboxSession Create() => session;
    }

    private sealed class FakeMailboxSession : IMailboxSession
    {
        public List<string> Operations { get; } = [];
        public string? LastCredential { get; private set; }
        public Exception? AuthenticateFailure { get; init; }

        public Task ConnectAsync(MailboxConnectionSettings settings, CancellationToken cancellationToken)
        {
            Operations.Add("connect");
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(string userName, string secret, CancellationToken cancellationToken)
        {
            Operations.Add("authenticate");
            LastCredential = secret;
            return AuthenticateFailure is null ? Task.CompletedTask : Task.FromException(AuthenticateFailure);
        }

        public Task IdentifyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<MailboxSessionInfo> OpenReadOnlyAsync(string mailboxName, CancellationToken cancellationToken)
        {
            Operations.Add($"open:{mailboxName}");
            return Task.FromResult(new MailboxSessionInfo(1));
        }

        public Task<MailboxSearchResult> SearchAsync(MailboxSearchCriteria criteria, CancellationToken cancellationToken)
            => Task.FromResult(new MailboxSearchResult([], []));

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Operations.Add("disconnect");
            return Task.CompletedTask;
        }
    }
}