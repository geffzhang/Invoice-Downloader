using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace InvoiceFlowAI.Infrastructure.Url;

internal static class PublicDnsOverHttpsResolver
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly (string Host, IPAddress Address)[] Servers =
    [
        ("cloudflare-dns.com", IPAddress.Parse("1.1.1.1")),
        ("dns.google", IPAddress.Parse("8.8.8.8")),
        ("dns.quad9.net", IPAddress.Parse("9.9.9.9")),
    ];

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ConnectCallback = ConnectToPinnedResolverAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        foreach (var server in Servers)
        {
            var answers = new List<IPAddress>();
            foreach (var recordType in new[] { "A", "AAAA" })
            {
                try
                {
                    var endpoint = $"https://{server.Host}/dns-query?name={Uri.EscapeDataString(host)}&type={recordType}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    request.Headers.Accept.ParseAdd("application/dns-json");
                    using var response = await Client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode
                        || response.Content.Headers.ContentLength is > MaxResponseBytes)
                    {
                        continue;
                    }

                    var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (payload.Length > MaxResponseBytes) continue;
                    using var document = JsonDocument.Parse(payload);
                    if (!document.RootElement.TryGetProperty("Status", out var status)
                        || status.GetInt32() != 0
                        || !document.RootElement.TryGetProperty("Answer", out var answerArray)
                        || answerArray.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var answer in answerArray.EnumerateArray())
                    {
                        if (!answer.TryGetProperty("type", out var type)
                            || type.GetInt32() is not (1 or 28)
                            || !answer.TryGetProperty("data", out var data)
                            || !IPAddress.TryParse(data.GetString(), out var address))
                        {
                            continue;
                        }

                        answers.Add(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    break;
                }
            }

            if (answers.Count > 0) return answers.Distinct().ToArray();
        }

        throw new HttpRequestException("Public DNS attestation is unavailable.");
    }

    private static async ValueTask<Stream> ConnectToPinnedResolverAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var server = Servers.FirstOrDefault(item => item.Host.Equals(context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase));
        if (server.Host is null || context.DnsEndPoint.Port != 443)
        {
            throw new HttpRequestException("The DNS attestor endpoint is not approved.");
        }

        var socket = new Socket(server.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(server.Address, 443), cancellationToken).ConfigureAwait(false);
            if (socket.RemoteEndPoint is not IPEndPoint peer || !peer.Address.Equals(server.Address) || peer.Port != 443)
            {
                throw new HttpRequestException("The DNS attestor peer could not be verified.");
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}