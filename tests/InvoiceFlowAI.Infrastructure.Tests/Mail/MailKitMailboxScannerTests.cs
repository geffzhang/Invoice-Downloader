using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Runs;
using InvoiceFlowAI.Infrastructure.Mail;
using MailKit;
using MailKit.Security;
using MailKit.Search;
using MimeKit;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Mail;

public sealed class MailKitMailboxScannerTests
{
    [Fact]
    public async Task Missing_account_maps_to_mailbox_account_not_found()
    {
        var scanner = CreateScanner(account: null);

        var act = () => scanner.ScanAsync(new MailboxScanRequest("missing", null, null, null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MailboxScanException>();
        exception.Which.ReasonCode.Should().Be("MAILBOX_ACCOUNT_NOT_FOUND");
        exception.Which.Message.Should().NotContain("missing");
    }

    [Fact]
    public async Task Empty_credential_name_maps_to_credentials_not_configured()
    {
        var account = NewAccount() with { CredentialName = "" };
        var scanner = CreateScanner(account: account);

        var act = () => scanner.ScanAsync(new MailboxScanRequest(account.AccountId, null, null, null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MailboxScanException>();
        exception.Which.ReasonCode.Should().Be("CREDENTIALS_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Missing_secret_maps_to_credentials_not_configured()
    {
        var account = NewAccount();
        var scanner = CreateScanner(account: account, secrets: new Dictionary<string, string?>());

        var act = () => scanner.ScanAsync(new MailboxScanRequest(account.AccountId, null, null, null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MailboxScanException>();
        exception.Which.ReasonCode.Should().Be("CREDENTIALS_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Account_reader_operation_cancellation_is_preserved()
    {
        var scanner = CreateScanner(accountReader: new ThrowingMailboxAccountReader(new OperationCanceledException("account reader canceled")));

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Account_reader_failure_maps_to_safe_protocol_failed_message()
    {
        var scanner = CreateScanner(accountReader: new ThrowingMailboxAccountReader(new InvalidOperationException("reader leak C:\\secrets\\alice@example.com auth-code")));

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MailboxScanException>();
        exception.Which.ReasonCode.Should().Be("IMAP_PROTOCOL_FAILED");
        exception.Which.InnerException.Should().BeNull();
        exception.Which.Message.Should().Be("Mailbox scan failed.");
        exception.Which.ToString().Should().NotContain("alice@example.com");
        exception.Which.ToString().Should().NotContain("auth-code");
        exception.Which.ToString().Should().NotContain("C:\\secrets");
    }

    [Fact]
    public async Task Secret_store_operation_cancellation_is_preserved()
    {
        var scanner = CreateScanner(secretStore: new ThrowingSecretStore(new OperationCanceledException("secret store canceled")));

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Secret_store_failure_maps_to_safe_credentials_not_configured_message()
    {
        var scanner = CreateScanner(secretStore: new ThrowingSecretStore(new InvalidOperationException("secret leak C:\\vault\\alice@example.com auth-code")));

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MailboxScanException>();
        exception.Which.ReasonCode.Should().Be("CREDENTIALS_NOT_CONFIGURED");
        exception.Which.InnerException.Should().BeNull();
        exception.Which.Message.Should().Be("Mailbox credentials are not configured.");
        exception.Which.ToString().Should().NotContain("alice@example.com");
        exception.Which.ToString().Should().NotContain("auth-code");
        exception.Which.ToString().Should().NotContain("C:\\vault");
    }

    [Fact]
    public async Task Account_from_163_sends_id_but_qq_does_not()
    {
        var session163 = new FakeMailboxSession();
        var scanner163 = CreateScanner(
            account: NewAccount(emailAddress: "alice@163.com"),
            session: session163);

        await scanner163.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        session163.IdentifyCallCount.Should().Be(1);

        var sessionQq = new FakeMailboxSession();
        var scannerQq = CreateScanner(
            account: NewAccount(emailAddress: "alice@qq.com"),
            session: sessionQq);

        await scannerQq.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        sessionQq.IdentifyCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Configured_mailbox_is_selected_otherwise_inbox()
    {
        var explicitSession = new FakeMailboxSession();
        var explicitScanner = CreateScanner(
            account: NewAccount(defaultMailbox: "Invoices"),
            session: explicitSession);

        await explicitScanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        explicitSession.OpenedMailbox.Should().Be("Invoices");

        var defaultSession = new FakeMailboxSession();
        var defaultScanner = CreateScanner(
            account: NewAccount(defaultMailbox: null),
            session: defaultSession);

        await defaultScanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        defaultSession.OpenedMailbox.Should().Be("INBOX");
    }

    [Fact]
    public async Task Matching_uid_validity_searches_after_since_uid_and_retains_false_change_flag()
    {
        var session = new FakeMailboxSession
        {
            SessionInfo = new MailboxSessionInfo(77),
            SearchResults = [NewMessage(uid: 105)]
        };
        var scanner = CreateScanner(session: session);

        var result = await scanner.ScanAsync(
            new MailboxScanRequest("acct-1", new DateOnly(2026, 9, 1), 104, "77"),
            CancellationToken.None);

        session.LastSearchCriteria.Should().BeEquivalentTo(new MailboxSearchCriteria(104, new DateOnly(2026, 9, 1)));
        result.UidValidity.Should().Be("77");
        result.UidValidityChanged.Should().BeFalse();
        result.HighestUid.Should().Be(105);
        result.AccountId.Should().Be("acct-1");
    }

    [Fact]
    public async Task Scan_preserves_fetch_failures_and_advances_highest_uid()
    {
        var session = new FakeMailboxSession
        {
            SearchResults = [NewMessage(uid: 105)],
            FetchFailures = [new MailboxFetchFailure(106, "IMAP_MESSAGE_FETCH_FAILED")],
        };
        var scanner = CreateScanner(session: session);

        var result = await scanner.ScanAsync(
            new MailboxScanRequest("acct-1", null, null, null),
            CancellationToken.None);

        result.Messages.Select(message => message.Uid).Should().Equal("105");
        result.FetchFailures.Should().ContainSingle().Which.Uid.Should().Be(106);
        result.HighestUid.Should().Be(106);
    }

    [Fact]
    public async Task Changed_uid_validity_ignores_since_uid_retains_since_date_and_sets_change_flag()
    {
        var session = new FakeMailboxSession
        {
            SessionInfo = new MailboxSessionInfo(88),
            SearchResults = []
        };
        var scanner = CreateScanner(session: session);

        var result = await scanner.ScanAsync(
            new MailboxScanRequest("acct-1", new DateOnly(2026, 9, 2), 104, "77"),
            CancellationToken.None);

        session.LastSearchCriteria.Should().BeEquivalentTo(new MailboxSearchCriteria(null, new DateOnly(2026, 9, 2)));
        result.UidValidity.Should().Be("88");
        result.UidValidityChanged.Should().BeTrue();
        result.HighestUid.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public async Task Positive_since_uid_with_missing_or_invalid_uid_validity_disables_uid_reuse(string? requestUidValidity)
    {
        var session = new FakeMailboxSession
        {
            SessionInfo = new MailboxSessionInfo(88),
            SearchResults = []
        };
        var scanner = CreateScanner(session: session);

        var result = await scanner.ScanAsync(
            new MailboxScanRequest("acct-1", new DateOnly(2026, 9, 2), 104, requestUidValidity),
            CancellationToken.None);

        session.LastSearchCriteria.Should().BeEquivalentTo(new MailboxSearchCriteria(null, new DateOnly(2026, 9, 2)));
        result.UidValidityChanged.Should().BeTrue();
        result.HighestUid.Should().Be(0);
    }

    [Fact]
    public async Task Matching_uid_validity_with_no_new_messages_retains_requested_highest_uid()
    {
        var session = new FakeMailboxSession
        {
            SessionInfo = new MailboxSessionInfo(77),
            SearchResults = []
        };
        var scanner = CreateScanner(session: session);

        var result = await scanner.ScanAsync(
            new MailboxScanRequest("acct-1", null, 104, "77"),
            CancellationToken.None);

        result.HighestUid.Should().Be(104);
    }

    [Fact]
    public async Task Negative_since_uid_is_normalized_for_search_criteria_and_empty_result_highest_uid()
    {
        var session = new FakeMailboxSession
        {
            SessionInfo = new MailboxSessionInfo(77),
            SearchResults = []
        };
        var scanner = CreateScanner(session: session);

        var result = await scanner.ScanAsync(
            new MailboxScanRequest("acct-1", new DateOnly(2026, 9, 3), -1, "77"),
            CancellationToken.None);

        session.LastSearchCriteria.Should().BeEquivalentTo(new MailboxSearchCriteria(null, new DateOnly(2026, 9, 3)));
        result.HighestUid.Should().Be(0);
    }

    [Fact]
    public async Task Attachments_are_tiered_policy_classified_and_payloads_are_immutable()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var session = new FakeMailboxSession
        {
            SessionInfo = new MailboxSessionInfo(77),
            SearchResults =
            [
                NewMessage(
                    uid: 200,
                    subject: "Invoice enclosed",
                    bodyText: "body keyword",
                    attachments:
                    [
                        new MailboxFetchedAttachment("invoice.png", "image/png", "attachment", payload)
                    ])
            ]
        };
        var policy = new FakeAttachmentCandidatePolicy();
        var scanner = CreateScanner(
            session: session,
            attachmentPolicy: policy,
            imageInfoProvider: _ => new AttachmentImageInfo(320, 200));

        var result = await scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        result.Messages.Should().ContainSingle();
        result.Attachments.Should().ContainSingle();
        var candidate = result.Attachments[0];
        candidate.EmailTier.Should().Be(2);
        candidate.Mailbox.Should().Be("INBOX");
        candidate.MessageUid.Should().Be("200");
        candidate.Decision.ReasonCode.Should().Be("TEST_POLICY");
        candidate.Decision.ImageInfo.Should().BeEquivalentTo(new AttachmentImageInfo(320, 200));
        policy.LastInput.Should().NotBeNull();
        policy.LastInput!.ImageInfo.Should().BeEquivalentTo(new AttachmentImageInfo(320, 200));
        candidate.Payload.ToArray().Should().Equal(1, 2, 3, 4);

        payload[0] = 99;
        candidate.Payload.ToArray().Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Url_candidates_are_projected_from_plain_text_and_html_without_retaining_bodies()
    {
        var plainUrl = "https://invoices.example.com/download?id=plain-token";
        var htmlUrl = "https://fp.bwjf.cn/downsigninvoice?code=html-token&amp;format=pdf";
        var session = new FakeMailboxSession
        {
            SessionInfo = new MailboxSessionInfo(77),
            SearchResults =
            [
                NewMessage(uid: 201, bodyText: $"Invoice: {plainUrl}") with
                {
                    HtmlBody = $"<a href=\"{htmlUrl}\">Download invoice</a><a href=\"{htmlUrl}\">again</a>"
                }
            ]
        };
        var scanner = CreateScanner(session: session);

        var result = await scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        result.UrlCandidates.Should().HaveCount(2);
        result.UrlCandidates[0].SourceUrl.ToString().Should().Be(plainUrl);
        result.UrlCandidates[0].ProviderFamily.Should().BeEmpty();
        result.UrlCandidates[1].SourceUrl.ToString().Should().Be("https://fp.bwjf.cn/downsigninvoice?code=html-token&format=pdf");
        result.UrlCandidates[1].ProviderFamily.Should().Be("bwjf_signed_invoice");
        result.UrlCandidates.Should().OnlyContain(candidate =>
            candidate.AccountId == "acct-1"
            && candidate.Mailbox == "INBOX"
            && candidate.UidValidity == "77"
            && candidate.MessageUid == "201");
        result.UrlCandidates.SelectMany(candidate => candidate.ExpectedFields.Values)
            .Should().NotContain(value => value.Contains("Invoice:", StringComparison.Ordinal));
        result.UrlCandidates.Should().OnlyContain(candidate => candidate.SourceUrl.Query.Length > 0);
    }

    [Fact]
    public async Task Direct_provider_expected_fields_are_extracted_without_copying_message_body()
    {
        var sourceUrl = new Uri("https://dppt.beijing.chinatax.gov.cn/kpfw/fpjfzz/v1/exportdzfpwjewm?Wjgs=xml");
        var session = new FakeMailboxSession
        {
            SearchResults =
            [
                NewMessage(
                    uid: 202,
                    subject: "发票号码：12345678901234567890",
                    bodyText: "开票日期：2026-09-24 请下载附件") with
                {
                    HtmlBody = $"<a href=\"{sourceUrl}\">XML invoice</a>"
                }
            ]
        };
        var scanner = CreateScanner(session: session);

        var result = await scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        var candidate = result.UrlCandidates.Should().ContainSingle().Subject;
        candidate.ProviderFamily.Should().Be("chinatax_direct_invoice");
        candidate.ExpectedFields.Should().Contain(new KeyValuePair<string, string>("invoice_number", "12345678901234567890"));
        candidate.ExpectedFields.Should().Contain(new KeyValuePair<string, string>("invoice_date", "2026-09-24"));
        candidate.ExpectedFields.Should().Contain(new KeyValuePair<string, string>("preferred_kind", "xml"));
        candidate.ExpectedFields.Should().NotContainKey("body");
        candidate.ExpectedFields.Values.Should().NotContain("开票日期：2026-09-24 请下载附件");
    }

    [Fact]
    public async Task Expected_field_evidence_retains_url_subject_and_body_sources_in_priority_order()
    {
        var sourceUrl = new Uri("https://dppt.beijing.chinatax.gov.cn/kpfw/fpjfzz/v1/exportdzfpwjewm?Fphm=11111111111111111111");
        var session = new FakeMailboxSession
        {
            SearchResults =
            [
                NewMessage(
                    uid: 209,
                    subject: "发票号码：22222222222222222222",
                    bodyText: "发票号码：33333333333333333333") with
                {
                    HtmlBody = $"<a href=\"{sourceUrl}\">Download</a>"
                }
            ]
        };
        var scanner = CreateScanner(session: session);

        var result = await scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        var candidate = result.UrlCandidates.Should().ContainSingle().Subject;
        candidate.ExpectedFields["invoice_number"].Should().Be("11111111111111111111");
        candidate.ExpectedFieldEvidence["invoice_number"].Should().Equal(
            new ExpectedFieldEvidence("11111111111111111111", ExpectedFieldSource.UrlQuery, 0),
            new ExpectedFieldEvidence("22222222222222222222", ExpectedFieldSource.Subject, 0),
            new ExpectedFieldEvidence("33333333333333333333", ExpectedFieldSource.Body, 0));
        candidate.ExpectedFieldEvidence.Values.SelectMany(evidence => evidence)
            .Should().NotContain(evidence => evidence.Value.Contains("发票号码", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Authentication_rejection_maps_to_safe_login_failed_message()
    {
        var session = new FakeMailboxSession
        {
            AuthenticateException = new AuthenticationException("Login failed for alice@example.com with secret 123456"),
            DisconnectException = new InvalidOperationException("disconnect leak")
        };
        var scanner = CreateScanner(session: session);

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MailboxScanException>();
        exception.Which.ReasonCode.Should().Be("IMAP_LOGIN_FAILED");
        exception.Which.InnerException.Should().BeNull();
        exception.Which.Message.Should().NotContain("alice@example.com");
        exception.Which.Message.Should().NotContain("123456");
        exception.Which.ToString().Should().NotContain("alice@example.com");
        exception.Which.ToString().Should().NotContain("123456");
        session.DisconnectCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_is_preserved()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var scanner = CreateScanner();

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Cancellation_from_session_operation_is_preserved_when_disconnect_also_fails()
    {
        var session = new FakeMailboxSession
        {
            SearchException = new OperationCanceledException("scan canceled"),
            DisconnectException = new InvalidOperationException("disconnect leak")
        };
        var scanner = CreateScanner(session: session);

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        session.DisconnectCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_during_disconnect_is_propagated_when_scan_succeeds()
    {
        using var cts = new CancellationTokenSource();
        var session = new FakeMailboxSession
        {
            SearchResults = [],
            CancelTokenAfterSearch = cts,
            ThrowIfCancellationRequestedOnDisconnect = true
        };
        var scanner = CreateScanner(session: session);

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        session.DisconnectCallCount.Should().Be(1);
        session.DisconnectCancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task Disconnect_happens_in_finally_when_protocol_fails()
    {
        var session = new FakeMailboxSession
        {
            SearchException = new InvalidOperationException("protocol leak")
        };
        var scanner = CreateScanner(session: session);

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        await act.Should().ThrowAsync<MailboxScanException>()
            .Where(ex => ex.ReasonCode == "IMAP_PROTOCOL_FAILED");
        session.DisconnectCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Existing_mailbox_scan_exception_is_preserved_when_disconnect_also_fails()
    {
        var expected = new MailboxScanException("SAFE_REASON", "safe message");
        var session = new FakeMailboxSession
        {
            SearchException = expected,
            DisconnectException = new InvalidOperationException("disconnect leak")
        };
        var scanner = CreateScanner(session: session);

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MailboxScanException>();
        exception.Which.Should().BeSameAs(expected);
        session.DisconnectCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Protocol_failure_mapping_is_preserved_when_disconnect_also_fails()
    {
        var session = new FakeMailboxSession
        {
            SearchException = new InvalidOperationException("protocol leak alice@example.com 123456"),
            DisconnectException = new InvalidOperationException("disconnect leak")
        };
        var scanner = CreateScanner(session: session);

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MailboxScanException>();
        exception.Which.ReasonCode.Should().Be("IMAP_PROTOCOL_FAILED");
        exception.Which.InnerException.Should().BeNull();
        exception.Which.ToString().Should().NotContain("alice@example.com");
        exception.Which.ToString().Should().NotContain("123456");
        session.DisconnectCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Success_path_disconnect_failure_maps_to_protocol_failed()
    {
        var session = new FakeMailboxSession
        {
            DisconnectException = new InvalidOperationException("disconnect leak alice@example.com 123456")
        };
        var scanner = CreateScanner(session: session);

        var act = () => scanner.ScanAsync(new MailboxScanRequest("acct-1", null, null, null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MailboxScanException>();
        exception.Which.ReasonCode.Should().Be("IMAP_PROTOCOL_FAILED");
        exception.Which.InnerException.Should().BeNull();
        exception.Which.ToString().Should().NotContain("alice@example.com");
        exception.Which.ToString().Should().NotContain("123456");
        session.DisconnectCallCount.Should().Be(1);
    }

    private static MailKitMailboxScanner CreateScanner(
        MailboxConnectionSettings? account = null,
        Dictionary<string, string?>? secrets = null,
        FakeMailboxSession? session = null,
        IEmailTierClassifier? tierClassifier = null,
        IAttachmentCandidatePolicy? attachmentPolicy = null,
        IMailboxAccountReader? accountReader = null,
        ISecretStore? secretStore = null,
        Func<ReadOnlyMemory<byte>, AttachmentImageInfo?>? imageInfoProvider = null)
    {
        account ??= NewAccount();
        secrets ??= new Dictionary<string, string?> { [account.CredentialName] = "auth-code" };
        session ??= new FakeMailboxSession();

        return new MailKitMailboxScanner(
            accountReader ?? (account is null ? new FakeMailboxAccountReader(null) : new FakeMailboxAccountReader(account)),
            secretStore ?? new FakeSecretStore(secrets),
            new MailboxChannelRegistry(),
            tierClassifier ?? new EmailTierClassifier(),
            attachmentPolicy ?? new AttachmentCandidatePolicy(),
            new FakeMailboxSessionFactory(session),
            imageInfoProvider ?? (_ => null));
    }

    private static MailboxConnectionSettings NewAccount(
        string accountId = "acct-1",
        string emailAddress = "alice@example.com",
        string credentialName = "mail.imap.auth-code",
        string? defaultMailbox = null)
        => new(
            accountId,
            emailAddress,
            "imap.saved.example.com",
            993,
            true,
            credentialName,
            defaultMailbox);

    private static MailboxFetchedMessage NewMessage(
        long uid,
        string subject = "hello",
        string from = "sender@example.com",
        string bodyText = "plain body",
        IReadOnlyList<MailboxFetchedAttachment>? attachments = null)
        => new(
            uid,
            new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero),
            subject,
            from,
            bodyText,
            attachments ?? []);

    private sealed class FakeMailboxAccountReader : IMailboxAccountReader
    {
        private readonly MailboxConnectionSettings? _account;

        public FakeMailboxAccountReader(MailboxConnectionSettings? account) => _account = account;

        public Task<IReadOnlyList<InvoiceFlowAI.Contracts.Accounts.MailboxAccountSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InvoiceFlowAI.Contracts.Accounts.MailboxAccountSnapshot>>([]);

        public Task<MailboxConnectionSettings?> FindAsync(string accountId, CancellationToken cancellationToken)
            => Task.FromResult(_account is not null && _account.AccountId == accountId ? _account : null);
    }

    private sealed class ThrowingMailboxAccountReader : IMailboxAccountReader
    {
        private readonly Exception _exception;

        public ThrowingMailboxAccountReader(Exception exception) => _exception = exception;

        public Task<IReadOnlyList<InvoiceFlowAI.Contracts.Accounts.MailboxAccountSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InvoiceFlowAI.Contracts.Accounts.MailboxAccountSnapshot>>([]);

        public Task<MailboxConnectionSettings?> FindAsync(string accountId, CancellationToken cancellationToken)
            => Task.FromException<MailboxConnectionSettings?>(_exception);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string?> _secrets;

        public FakeSecretStore(Dictionary<string, string?> secrets) => _secrets = secrets;

        public Task SaveAsync(string name, string value, CancellationToken cancellationToken)
        {
            _secrets[name] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult(_secrets.TryGetValue(name, out var value) ? value : null);

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            _secrets.Remove(name);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSecretStore : ISecretStore
    {
        private readonly Exception _exception;

        public ThrowingSecretStore(Exception exception) => _exception = exception;

        public Task SaveAsync(string name, string value, CancellationToken cancellationToken)
            => Task.FromException(_exception);

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
            => Task.FromException<string?>(_exception);

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
            => Task.FromException(_exception);
    }

    private sealed class FakeMailboxSessionFactory : IMailboxSessionFactory
    {
        private readonly IMailboxSession _session;

        public FakeMailboxSessionFactory(IMailboxSession session) => _session = session;

        public IMailboxSession Create() => _session;
    }

    private sealed class FakeMailboxSession : IMailboxSession
    {
        public MailboxSessionInfo SessionInfo { get; set; } = new(77);
        public IReadOnlyList<MailboxFetchedMessage> SearchResults { get; set; } = [];
        public IReadOnlyList<MailboxFetchFailure> FetchFailures { get; set; } = [];
        public Exception? AuthenticateException { get; set; }
        public Exception? SearchException { get; set; }
        public Exception? DisconnectException { get; set; }
        public bool ThrowIfCancellationRequestedOnDisconnect { get; set; }
        public CancellationTokenSource? CancelTokenAfterSearch { get; set; }
        public int IdentifyCallCount { get; private set; }
        public int DisconnectCallCount { get; private set; }
        public string? OpenedMailbox { get; private set; }
        public MailboxSearchCriteria? LastSearchCriteria { get; private set; }
        public CancellationToken DisconnectCancellationToken { get; private set; }

        public Task ConnectAsync(MailboxConnectionSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AuthenticateAsync(string userName, string secret, CancellationToken cancellationToken)
        {
            if (AuthenticateException is not null)
            {
                throw AuthenticateException;
            }

            return Task.CompletedTask;
        }

        public Task IdentifyAsync(CancellationToken cancellationToken)
        {
            IdentifyCallCount++;
            return Task.CompletedTask;
        }

        public Task<MailboxSessionInfo> OpenReadOnlyAsync(string mailboxName, CancellationToken cancellationToken)
        {
            OpenedMailbox = mailboxName;
            return Task.FromResult(SessionInfo);
        }

        public Task<MailboxSearchResult> SearchAsync(MailboxSearchCriteria criteria, CancellationToken cancellationToken)
        {
            LastSearchCriteria = criteria;
            if (SearchException is not null)
            {
                return Task.FromException<MailboxSearchResult>(SearchException);
            }

            CancelTokenAfterSearch?.Cancel();

            return Task.FromResult(new MailboxSearchResult(SearchResults, FetchFailures));
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            DisconnectCallCount++;
            DisconnectCancellationToken = cancellationToken;

            if (ThrowIfCancellationRequestedOnDisconnect)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (DisconnectException is not null)
            {
                throw DisconnectException;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeAttachmentCandidatePolicy : IAttachmentCandidatePolicy
    {
        public AttachmentCandidateInput? LastInput { get; private set; }

        public AttachmentCandidateDecision Classify(AttachmentCandidateInput input)
        {
            LastInput = input;
            return new AttachmentCandidateDecision(
                "B",
                "main_chain",
                "TEST_POLICY",
                [],
                [],
                null,
                input.ImageInfo);
        }
    }
}

public sealed class MailKitMailboxSessionPureTests
{
    [Fact]
    public async Task Session_retries_a_transient_uid_fetch_once()
    {
        var attempts = new List<uint>();

        var result = await MailKitMailboxSession.SearchAndFetchAsync(
            new MailboxSearchCriteria(null, null),
            (_, _) => Task.FromResult<IReadOnlyList<UniqueId>>([new UniqueId(8)]),
            (_, _) => Task.FromResult<IReadOnlyList<MailboxMessageDateSummary>>([]),
            (uid, _) =>
            {
                attempts.Add(uid.Id);
                return attempts.Count == 1
                    ? Task.FromException<MimeMessage>(new IOException("temporary fetch failure"))
                    : Task.FromResult(new MimeMessage { Subject = "recovered" });
            },
            CancellationToken.None);

        attempts.Should().Equal(8u, 8u);
        result.Messages.Should().ContainSingle().Which.Subject.Should().Be("recovered");
        result.FetchFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task Session_preserves_successful_uids_and_reports_one_bounded_fetch_failure()
    {
        var uids = new[] { new UniqueId(1), new UniqueId(2), new UniqueId(3) };
        var fetchAttempts = new List<uint>();

        var result = await MailKitMailboxSession.SearchAndFetchAsync(
            new MailboxSearchCriteria(null, null),
            (_, _) => Task.FromResult<IReadOnlyList<UniqueId>>(uids),
            (_, _) => Task.FromResult<IReadOnlyList<MailboxMessageDateSummary>>([]),
            (uid, _) =>
            {
                fetchAttempts.Add(uid.Id);
                if (uid.Id == 2)
                {
                    return Task.FromException<MimeMessage>(
                        new InvalidOperationException("fetch failed for private@example.com token=secret"));
                }

                return Task.FromResult(new MimeMessage { Subject = $"message-{uid.Id}" });
            },
            CancellationToken.None);

        result.Messages.Select(message => message.Uid).Should().Equal(1L, 3L);
        result.FetchFailures.Should().ContainSingle().Which.Uid.Should().Be(2);
        result.FetchFailures[0].ReasonCode.Should().Be("IMAP_MESSAGE_FETCH_FAILED");
        fetchAttempts.Should().Equal(1u, 2u, 2u, 3u);
    }

    [Fact]
    public async Task Session_summary_uid_selection_matches_full_message_fetch_set()
    {
        var uids = Enumerable.Range(1, 5).Select(value => new UniqueId((uint)value)).ToArray();
        var summaryRequests = new List<uint>();
        var fullMessageRequests = new List<uint>();
        var summaries = new[]
        {
            new MailboxMessageDateSummary(uids[0], new DateTimeOffset(2026, 6, 9, 16, 0, 0, TimeSpan.Zero), null),
            new MailboxMessageDateSummary(uids[1], new DateTimeOffset(2026, 6, 9, 15, 59, 59, TimeSpan.Zero), null),
            new MailboxMessageDateSummary(uids[2], null, new DateTimeOffset(2026, 6, 11, 16, 0, 0, TimeSpan.Zero)),
            new MailboxMessageDateSummary(uids[3], null, null),
            new MailboxMessageDateSummary(uids[4], new DateTimeOffset(2026, 6, 10, 4, 0, 0, TimeSpan.Zero), null),
        };
        var criteria = new MailboxSearchCriteria(null, new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 11));

        var result = await MailKitMailboxSession.SearchAndFetchAsync(
            criteria,
            (_, _) => Task.FromResult<IReadOnlyList<UniqueId>>(uids),
            (requested, _) =>
            {
                summaryRequests.AddRange(requested.Select(uid => uid.Id));
                return Task.FromResult<IReadOnlyList<MailboxMessageDateSummary>>(summaries);
            },
            (uid, _) =>
            {
                fullMessageRequests.Add(uid.Id);
                var message = new MimeMessage { Subject = $"message-{uid.Id}" };
                return Task.FromResult(message);
            },
            CancellationToken.None);

        summaryRequests.Should().Equal(1u, 2u, 3u, 4u, 5u);
        fullMessageRequests.Should().Equal(1u, 4u, 5u);
        result.Messages.Select(message => message.Uid).Should().Equal(1L, 4L, 5L);
    }

    [Theory]
    [InlineData("https://dppt.beijing.chinatax.gov.cn/kpfw/fpjfzz/v1/exportdzfpwjewm", "chinatax_direct_invoice")]
    [InlineData("https://fp.bwjf.cn/downsigninvoice?code=x", "bwjf_signed_invoice")]
    [InlineData("https://sdapi.fpyun.com.cn/invoice/qd/download/getinvoicefile", "fpyun_direct_invoice")]
    [InlineData("https://nnfp.jss.com.cn/scan-invoice/printqrcode", "nuonuo_scan_invoice")]
    [InlineData("https://files.pdd-fapiao.com/invoice/pdf/a.pdf", "pdd_direct_invoice")]
    [InlineData("https://eicore-invoice-01.s3.cn-north-1.jdcloud-oss.com/digital-invoice/a.pdf", "jdcloud_direct_invoice")]
    [InlineData("https://etd.kpbyd.com/hub/files/download?fileCode=abc_pdf", "kpbyd_direct_invoice")]
    public void Provider_family_detection_matches_supported_direct_invoice_urls(string rawUrl, string expectedFamily)
    {
        var family = MailboxUrlCandidateDiscovery.DetectProviderFamily(new Uri(rawUrl), "sender@example.com", "Invoice");

        family.Should().Be(expectedFamily);
    }

    [Theory]
    [InlineData("http://sdapi.fpyun.com.cn/invoice/qd/download/getinvoicefile")]
    [InlineData("https://etd.kpbyd.com/hub/files/download?fileCode=abc_pdfx")]
    public void Provider_family_detection_rejects_nonmatching_scheme_or_query(string rawUrl)
    {
        var family = MailboxUrlCandidateDiscovery.DetectProviderFamily(new Uri(rawUrl), "sender@example.com", "Invoice");

        family.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://mail.example.invalid/previewinvoice?id=fixture", "Invoice")]
    [InlineData("https://mail.example.invalid/downloadpdf?id=fixture", "Invoice")]
    [InlineData("https://mail.example.invalid/document?id=fixture", "发票 下载通知")]
    public void Provider_family_detection_uses_baiwang_url_and_subject_signals(string rawUrl, string subject)
    {
        var family = MailboxUrlCandidateDiscovery.DetectProviderFamily(new Uri(rawUrl), "sender@example.com", subject);

        family.Should().Be("baiwang");
    }

    [Fact]
    public void Create_client_implementation_contains_only_fixed_product_metadata()
    {
        var implementation = MailKitMailboxSession.CreateClientImplementation();

        implementation.Name.Should().Be("InvoiceFlowAI");
        implementation.Version.Should().NotBeNullOrWhiteSpace();
        implementation.Properties.Keys.Should().OnlyContain(static key => key == "name" || key == "version");
    }

    [Fact]
    public void Project_message_prefers_text_body_and_includes_named_inline_parts()
    {
        var attachmentBytes = new byte[] { 7, 8, 9 };
        var inlineBytes = new byte[] { 1, 2, 3 };
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("billing@example.com"));
        message.Subject = "Invoice attached";
        message.Date = new DateTimeOffset(2026, 9, 24, 9, 30, 0, TimeSpan.FromHours(8));
        var body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "plain tier body" },
            CreatePart("invoice.pdf", "application", "pdf", ContentDisposition.Attachment, attachmentBytes),
            CreatePart("logo.png", "image", "png", ContentDisposition.Inline, inlineBytes)
        };
        message.Body = body;

        var projected = MailKitMailboxSession.ProjectMessage(42, message, null);

        projected.Uid.Should().Be(42);
        projected.FromAddress.Should().Be("billing@example.com");
        projected.BodyText.Should().Be("plain tier body");
        projected.Attachments.Should().HaveCount(2);
        projected.Attachments[0].FileName.Should().Be("invoice.pdf");
        projected.Attachments[0].ContentDisposition.Should().Be("attachment");
        projected.Attachments[0].Payload.Should().Equal(attachmentBytes);
        projected.Attachments[1].FileName.Should().Be("logo.png");
        projected.Attachments[1].ContentDisposition.Should().Be("inline");
        projected.Attachments[1].Payload.Should().Equal(inlineBytes);
    }

    [Fact]
    public void Project_message_prefers_header_date_over_internal_date()
    {
        var message = new MimeMessage
        {
            Date = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.FromHours(8)),
        };
        var internalDate = new DateTimeOffset(2026, 6, 11, 1, 0, 0, TimeSpan.Zero);

        var projected = MailKitMailboxSession.ProjectMessage(1, message, internalDate);

        projected.SentAtUtc.Should().Be(message.Date.ToUniversalTime());
    }

    [Fact]
    public void Project_message_falls_back_to_internal_date_when_header_date_is_missing()
    {
        var internalDate = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.FromHours(8));
        var message = new MimeMessage();
        message.Headers.Remove(HeaderId.Date);

        var projected = MailKitMailboxSession.ProjectMessage(1, message, internalDate);

        projected.SentAtUtc.Should().Be(internalDate.ToUniversalTime());
    }

    [Fact]
    public void Project_message_keeps_date_unknown_when_header_and_internal_date_are_missing()
    {
        var message = new MimeMessage();
        message.Headers.Remove(HeaderId.Date);

        var projected = MailKitMailboxSession.ProjectMessage(1, message, null);

        ((DateTimeOffset?)projected.SentAtUtc).Should().BeNull();
    }

    [Fact]
    public void Project_message_falls_back_to_html_body_with_tags_removed()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("billing@example.com"));
        message.Subject = "HTML only";
        message.Body = new TextPart("html") { Text = "<div>Hello <b>invoice</b> &amp; receipt</div>" };

        var projected = MailKitMailboxSession.ProjectMessage(43, message, null);

        projected.BodyText.Should().Be("Hello invoice & receipt");
        projected.HtmlBody.Should().Be("<div>Hello <b>invoice</b> &amp; receipt</div>");
    }

    [Fact]
    public void Project_message_projects_unnamed_attachment_parts_and_nested_messages_without_duplicating_body_parts()
    {
        var nested = new MimeMessage();
        nested.From.Add(MailboxAddress.Parse("nested@example.com"));
        nested.Subject = "Nested invoice";
        nested.Body = new TextPart("plain") { Text = "nested body" };

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("billing@example.com"));
        message.Subject = "Mixed attachments";
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "primary body" },
            CreateUnnamedAttachmentPart("application", "pdf", ContentDisposition.Attachment, [1, 2, 3]),
            CreateUnnamedAttachmentPart("image", "png", ContentDisposition.Inline, [4, 5, 6]),
            CreateMessageAttachmentPart(nested, null)
        };

        var projected = MailKitMailboxSession.ProjectMessage(44, message, null);

        projected.BodyText.Should().Be("primary body");
        projected.Attachments.Should().HaveCount(2);
        projected.Attachments[0].FileName.Should().Be("attachment-001.pdf");
        projected.Attachments[0].ContentType.Should().Be("application/pdf");
        projected.Attachments[0].ContentDisposition.Should().Be("attachment");
        projected.Attachments[0].Payload.Should().Equal(1, 2, 3);

        projected.Attachments[1].FileName.Should().Be("attachment-002.eml");
        projected.Attachments[1].ContentDisposition.Should().Be("attachment");
        projected.Attachments[1].Payload.Should().NotBeEmpty();

        using var nestedStream = new MemoryStream(projected.Attachments[1].Payload);
        var parsedNested = MimeMessage.Load(nestedStream);
        parsedNested.Subject.Should().Be("Nested invoice");
        parsedNested.TextBody.Should().Contain("nested body");
    }

    [Fact]
    public void Project_message_synthesizes_png_filename_and_policy_marks_tiny_image_as_extreme_negative_signal()
    {
        var payload = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("billing@example.com"));
        message.Subject = "Tiny image attachment";
        message.Body = new Multipart("mixed")
        {
            CreateUnnamedAttachmentPart("image", "png", ContentDisposition.Attachment, payload)
        };

        var projected = MailKitMailboxSession.ProjectMessage(45, message, null);

        projected.Attachments.Should().ContainSingle();
        var attachment = projected.Attachments[0];
        attachment.FileName.Should().Be("attachment-001.png");

        var policy = new AttachmentCandidatePolicy();
        var decision = policy.Classify(new AttachmentCandidateInput(
            attachment.FileName,
            attachment.Payload.Length,
            2,
            attachment.ContentType,
            attachment.ContentDisposition,
            "imap_attachment",
            new AttachmentImageInfo(2, 2)));

        decision.Bucket.Should().Be("A");
        decision.Action.Should().Be("drop");
        decision.ReasonCode.Should().Be("A_EXTREME_NEGATIVE_SIGNAL");
    }

    [Theory]
    [InlineData("image", "jpeg", "attachment-001.jpg")]
    [InlineData("image", "png", "attachment-001.png")]
    [InlineData("image", "gif", "attachment-001.gif")]
    [InlineData("image", "bmp", "attachment-001.bmp")]
    [InlineData("image", "webp", "attachment-001.webp")]
    [InlineData("application", "pdf", "attachment-001.pdf")]
    [InlineData("application", "octet-stream", "attachment-001.bin")]
    public void Project_message_synthesizes_expected_extensions_for_known_mime_types(string mediaType, string mediaSubtype, string expectedFileName)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("billing@example.com"));
        message.Subject = "Synthetic attachment extension";
        message.Body = new Multipart("mixed")
        {
            CreateUnnamedAttachmentPart(mediaType, mediaSubtype, ContentDisposition.Attachment, [1, 2, 3])
        };

        var projected = MailKitMailboxSession.ProjectMessage(46, message, null);

        projected.Attachments.Should().ContainSingle();
        projected.Attachments[0].FileName.Should().Be(expectedFileName);
    }

    [Fact]
    public void Build_search_query_does_not_use_since_date_as_a_server_utc_filter()
    {
        var sinceDate = new DateOnly(2026, 9, 24);

        var query = MailKitMailboxSession.BuildSearchQuery(new MailboxSearchCriteria(-1, sinceDate));

        DescribeSearchQuery(query).Should().Be(DescribeSearchQuery(SearchQuery.All));
    }

    [Fact]
    public void Build_search_query_does_not_apply_server_date_filter_before_shanghai_local_filtering()
    {
        var query = MailKitMailboxSession.BuildSearchQuery(new MailboxSearchCriteria(
            null, new DateOnly(2026, 6, 10)));

        DescribeSearchQuery(query).Should().Be(DescribeSearchQuery(SearchQuery.All));
    }

    [Fact]
    public void Date_window_uses_shanghai_local_day_and_retains_unknown_dates()
    {
        var criteria = new MailboxSearchCriteria(null, new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 11));

        MailKitMailboxSession.IsInDateWindow(
            new DateTimeOffset(2026, 6, 9, 16, 0, 0, TimeSpan.Zero), criteria).Should().BeTrue();
        MailKitMailboxSession.IsInDateWindow(
            new DateTimeOffset(2026, 6, 9, 15, 59, 59, TimeSpan.Zero), criteria).Should().BeFalse();
        MailKitMailboxSession.IsInDateWindow(
            new DateTimeOffset(2026, 6, 11, 16, 0, 0, TimeSpan.Zero), criteria).Should().BeFalse();
        MailKitMailboxSession.IsInDateWindow(null, criteria).Should().BeTrue();
    }

    [Fact]
    public void Date_summary_filter_preserves_uid_order_and_keeps_unknown_after_out_of_range_messages()
    {
        var uids = Enumerable.Range(1, 5).Select(value => new UniqueId((uint)value)).ToArray();
        var summaries = new[]
        {
            new MailboxMessageDateSummary(uids[0],
                new DateTimeOffset(2026, 6, 9, 16, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero)),
            new MailboxMessageDateSummary(uids[1],
                new DateTimeOffset(2026, 6, 9, 15, 59, 59, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero)),
            new MailboxMessageDateSummary(uids[2], null,
                new DateTimeOffset(2026, 6, 11, 16, 0, 0, TimeSpan.Zero)),
            new MailboxMessageDateSummary(uids[3], null, null),
            new MailboxMessageDateSummary(uids[4],
                new DateTimeOffset(2026, 6, 10, 4, 0, 0, TimeSpan.Zero), null),
        };
        var criteria = new MailboxSearchCriteria(null, new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 11));

        MailKitMailboxSession.FilterUidsByDateWindow(uids, summaries, criteria)
            .Select(uid => uid.Id)
            .Should().Equal(1u, 4u, 5u);
    }

    [Fact]
    public void Build_search_query_treats_negative_uid_without_date_as_all_messages()
    {
        var query = MailKitMailboxSession.BuildSearchQuery(new MailboxSearchCriteria(-1, null));

        DescribeSearchQuery(query).Should().Be(DescribeSearchQuery(SearchQuery.All));
    }

    [Fact]
    public void Build_search_query_starts_from_uid_one_when_since_uid_is_zero()
    {
        var query = MailKitMailboxSession.BuildSearchQuery(new MailboxSearchCriteria(0, null));
        var expected = SearchQuery.All.And(SearchQuery.Uids(new UniqueIdRange(new MailKit.UniqueId(1), MailKit.UniqueId.MaxValue)));

        DescribeSearchQuery(query).Should().Be(DescribeSearchQuery(expected));
    }

    [Fact]
    public void Build_search_query_preserves_empty_result_semantics_at_or_above_uint_max()
    {
        var maxQuery = MailKitMailboxSession.BuildSearchQuery(new MailboxSearchCriteria(uint.MaxValue, null));
        var aboveMaxQuery = MailKitMailboxSession.BuildSearchQuery(new MailboxSearchCriteria((long)uint.MaxValue + 1, null));
        var expected = SearchQuery.All.And(SearchQuery.Not(SearchQuery.All));

        DescribeSearchQuery(maxQuery).Should().Be(DescribeSearchQuery(expected));
        DescribeSearchQuery(aboveMaxQuery).Should().Be(DescribeSearchQuery(expected));
    }

    [Fact]
    public void Filter_uids_after_cursor_removes_rfc_star_fallback_to_same_uid()
    {
        var filtered = FilterUidsAfterCursor([new MailKit.UniqueId(104)], 104);

        filtered.Should().BeEmpty();
    }

    [Fact]
    public void Filter_uids_after_cursor_keeps_only_strictly_newer_uids()
    {
        var filtered = FilterUidsAfterCursor([new MailKit.UniqueId(104), new MailKit.UniqueId(105)], 104);

        filtered.Select(static uid => uid.Id).Should().Equal(105u);
    }

    [Fact]
    public void Filter_uids_after_cursor_does_not_filter_when_cursor_is_missing_or_negative()
    {
        var uids = new[] { new MailKit.UniqueId(104), new MailKit.UniqueId(105) };

        FilterUidsAfterCursor(uids, null).Select(static uid => uid.Id).Should().Equal(104u, 105u);
        FilterUidsAfterCursor(uids, -1).Select(static uid => uid.Id).Should().Equal(104u, 105u);
    }

    private static IReadOnlyList<MailKit.UniqueId> FilterUidsAfterCursor(IReadOnlyList<MailKit.UniqueId> uids, long? sinceUid)
    {
        var method = typeof(MailKitMailboxSession).GetMethod(
            "FilterUidsAfterCursor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        method.Should().NotBeNull("SearchAsync must strictly filter returned UIDs after the cursor");

        var result = method!.Invoke(null, [uids, sinceUid]);
        return result.Should().BeAssignableTo<IReadOnlyList<MailKit.UniqueId>>().Subject;
    }

    private static string DescribeSearchQuery(SearchQuery query) => DescribeObject(query, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static string DescribeObject(object? value, HashSet<object> visited)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string text)
        {
            return $"\"{text}\"";
        }

        if (value is DateTime dateTime)
        {
            return dateTime.ToString("O");
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal || value is MailKit.UniqueId || value is UniqueIdRange)
        {
            return value.ToString() ?? type.FullName ?? type.Name;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            var parts = new List<string>();
            foreach (var item in enumerable)
            {
                parts.Add(DescribeObject(item, visited));
            }

            return $"[{string.Join(",", parts)}]";
        }

        if (!type.IsValueType && !visited.Add(value))
        {
            return $"<cycle:{type.FullName}>";
        }

        var fields = type
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(static field => !field.IsStatic)
            .OrderBy(static field => field.Name)
            .Select(field => $"{field.Name}={DescribeObject(field.GetValue(value), visited)}");

        return $"{type.FullName}{{{string.Join(";", fields)}}}";
    }

    private static MimePart CreatePart(string fileName, string mediaType, string mediaSubtype, string disposition, byte[] payload)
    {
        var part = new MimePart(mediaType, mediaSubtype)
        {
            FileName = fileName,
            ContentDisposition = new ContentDisposition(disposition),
            Content = new MimeContent(new MemoryStream(payload), ContentEncoding.Default)
        };

        return part;
    }

    private static MimePart CreateUnnamedAttachmentPart(string mediaType, string mediaSubtype, string disposition, byte[] payload)
    {
        var part = new MimePart(mediaType, mediaSubtype)
        {
            ContentDisposition = new ContentDisposition(disposition),
            Content = new MimeContent(new MemoryStream(payload), ContentEncoding.Default)
        };

        return part;
    }

    private static MessagePart CreateMessageAttachmentPart(MimeMessage nestedMessage, string? fileName)
    {
        var part = new MessagePart
        {
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            Message = nestedMessage
        };

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            part.ContentDisposition!.FileName = fileName;
        }

        return part;
    }
}