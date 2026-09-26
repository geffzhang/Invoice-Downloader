using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Infrastructure.Mail;
using MailKit;
using MailKit.Net.Imap;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Mail;

public sealed class MailKitMalformedSearchProtocolTests
{
    [Fact]
    public async Task Malformed_uid_search_wire_item_fails_closed_as_protocol_error()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunImapServerAsync(listener, malformedSearch: true, timeout.Token);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var session = new MailKitMailboxSession();

        try
        {
            await session.ConnectAsync(
                new MailboxConnectionSettings(
                    "synthetic-account",
                    "alice@fixture.invalid",
                    IPAddress.Loopback.ToString(),
                    endpoint.Port,
                    false,
                    "synthetic-credential",
                    "INBOX"),
                timeout.Token);
            await session.AuthenticateAsync("alice@fixture.invalid", "synthetic-secret", timeout.Token);
            await session.OpenReadOnlyAsync("INBOX", timeout.Token);

            var act = () => session.SearchAsync(new MailboxSearchCriteria(null, null, null), timeout.Token);

            await act.Should().ThrowAsync<ImapProtocolException>();
        }
        finally
        {
            await session.DisconnectAsync(CancellationToken.None);
            listener.Stop();
        }

        await serverTask;
    }

    [Fact]
    public async Task Internal_date_from_mailkit_wire_summary_is_used_for_date_filtering()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunImapServerAsync(listener, malformedSearch: false, timeout.Token);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var session = new MailKitMailboxSession();

        try
        {
            await session.ConnectAsync(
                new MailboxConnectionSettings(
                    "synthetic-account",
                    "alice@fixture.invalid",
                    IPAddress.Loopback.ToString(),
                    endpoint.Port,
                    false,
                    "synthetic-credential",
                    "INBOX"),
                timeout.Token);
            await session.AuthenticateAsync("alice@fixture.invalid", "synthetic-secret", timeout.Token);
            await session.OpenReadOnlyAsync("INBOX", timeout.Token);

            var result = await session.SearchAsync(
                new MailboxSearchCriteria(null, new DateOnly(2026, 9, 25), null),
                timeout.Token);

            result.Messages.Should().BeEmpty();
            result.FetchFailures.Should().BeEmpty();
        }
        finally
        {
            await session.DisconnectAsync(CancellationToken.None);
            listener.Stop();
        }

        await serverTask;
    }

    private static async Task RunImapServerAsync(
        TcpListener listener,
        bool malformedSearch,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };

        await writer.WriteLineAsync("* OK synthetic IMAP server ready");
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var separator = line.IndexOf(' ');
            if (separator <= 0)
            {
                continue;
            }

            var tag = line[..separator];
            var command = line[(separator + 1)..];
            if (command.StartsWith("CAPABILITY", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("* CAPABILITY IMAP4rev1");
                await writer.WriteLineAsync($"{tag} OK CAPABILITY completed");
            }
            else if (command.StartsWith("LOGIN", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync($"{tag} OK LOGIN completed");
            }
            else if (command.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("EXAMINE", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)");
                await writer.WriteLineAsync("* 0 EXISTS");
                await writer.WriteLineAsync("* 0 RECENT");
                await writer.WriteLineAsync("* OK [UIDVALIDITY 42] synthetic validity");
                await writer.WriteLineAsync("* OK [UIDNEXT 1] synthetic next UID");
                await writer.WriteLineAsync($"{tag} OK [READ-ONLY] SELECT completed");
            }
            else if (command.StartsWith("UID SEARCH", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync(malformedSearch ? "* SEARCH 90 malformed" : "* SEARCH 91");
                await writer.WriteLineAsync($"{tag} OK SEARCH completed");
            }
            else if (command.StartsWith("UID FETCH", StringComparison.OrdinalIgnoreCase)
                && command.Contains("INTERNALDATE", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("* 1 FETCH (UID 91 INTERNALDATE \"24-Sep-2026 08:00:00 +0000\")");
                await writer.WriteLineAsync($"{tag} OK FETCH completed");
            }
            else if (command.StartsWith("UID FETCH", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync($"{tag} BAD unexpected message body fetch");
            }
            else if (command.StartsWith("LOGOUT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("* BYE synthetic logout");
                await writer.WriteLineAsync($"{tag} OK LOGOUT completed");
                return;
            }
            else
            {
                await writer.WriteLineAsync($"{tag} BAD unsupported synthetic command");
            }
        }
    }
}