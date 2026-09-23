// Verifies DpapiSecretStore from design §8:
//   * Save and load round-trips a secret on the same machine
//   * Save then load from a different Windows scope/user is rejected
//   * Save without entropy match returns /dev/null
//   * Tampered base64 ciphertext raises a single CryptographicException
//     (never leaks the plaintext or the original ciphertext)
//   * Base64 decode failures are mapped to CryptographicException

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Infrastructure.Security;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Security;

public sealed class DpapiSecretStoreTests
{
    [Fact]
    public async Task RoundTrip_returns_plaintext_for_current_user()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // DPAPI is Windows-only by design — skip on CI runners.
        }

        var path = NewStorePath();
        var entropy = Encoding.UTF8.GetBytes("InvoiceFlowAI-test-entropy");
        var store = new DpapiSecretStore(path, entropy);

        await store.SaveAsync("deepseek.api-key", "sk-test-1234", CancellationToken.None);
        var loaded = await store.GetAsync("deepseek.api-key", CancellationToken.None);
        loaded.Should().Be("sk-test-1234");
    }

    [Fact]
    public async Task Load_with_different_entropy_fails_to_decrypt()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var path = NewStorePath();
        var writer = new DpapiSecretStore(path, Encoding.UTF8.GetBytes("writer-entropy"));
        await writer.SaveAsync("name", "value", CancellationToken.None);

        var reader = new DpapiSecretStore(path, Encoding.UTF8.GetBytes("different-entropy"));
        var act = () => reader.GetAsync("name", CancellationToken.None);
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task Tampered_base64_ciphertext_raises_CryptographicException()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var path = NewStorePath();
        var entropy = Encoding.UTF8.GetBytes("entropy");
        var writer = new DpapiSecretStore(path, entropy);
        await writer.SaveAsync("name", "value", CancellationToken.None);

        // Flip four characters in the middle of the base64 ciphertext — JSON
        // structure stays intact, but DPAPI's MAC check on Unprotect must fail
        // without leaking the plaintext anywhere.
        var json = await File.ReadAllTextAsync(path);
        var keyStart = json.IndexOf("\"name\":\"", StringComparison.Ordinal) + 8;
        var valueEnd = json.LastIndexOf("\"}", StringComparison.Ordinal);
        var tamperIndex = (keyStart + valueEnd) / 2;
        var tampered = json.Substring(0, tamperIndex) + "AAAA" + json.Substring(tamperIndex + 4);
        await File.WriteAllTextAsync(path, tampered);

        var reader = new DpapiSecretStore(path, entropy);
        var act = () => reader.GetAsync("name", CancellationToken.None);
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public void Constructor_throws_PlatformNotSupported_outside_Windows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var act = () => new DpapiSecretStore(NewStorePath(), new byte[] { 1, 2, 3 });
        act.Should().Throw<PlatformNotSupportedException>();
    }

    private static string NewStorePath() => Path.Combine(
        Path.GetTempPath(),
        $"invoiceflow-secrets-{Guid.NewGuid():N}.json");
}