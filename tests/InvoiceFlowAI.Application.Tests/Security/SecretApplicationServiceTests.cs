using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Security;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Security;

public sealed class SecretApplicationServiceTests
{
    [Theory]
    [InlineData("mail.imap.auth-code")]
    [InlineData("deepseek.api-key")]
    public async Task Set_persistent_stores_in_dpapi_layer_and_removes_session_override(string name)
    {
        var operations = new List<string>();
        var persistent = new FakePersistentSecretStore(operations);
        var session = new FakeSessionSecretStore(operations);
        await session.SaveAsync(name, "old-session-secret", CancellationToken.None);
        var service = new SecretApplicationService(persistent, session);

        var result = await service.SetAsync(
            new SecretSetRequest(name, "incoming-secret-value", SecretRetention.Persistent),
            CancellationToken.None);

        persistent.Values[name].Should().Be("incoming-secret-value");
        session.Values.Should().NotContainKey(name);
        operations.Should().ContainInOrder($"persistent.save:{name}", $"session.delete:{name}");
        result.Should().Be(new SecretMutationResult(name, Configured: true, Persistent: true));
        JsonSerializer.Serialize(result).Should().NotContain("incoming-secret-value");
    }

    [Fact]
    public async Task Set_session_deletes_persistent_secret_before_accepting_session_replacement()
    {
        var operations = new List<string>();
        var persistent = new FakePersistentSecretStore(operations);
        persistent.Values["mail.imap.auth-code"] = "old-persistent-secret";
        var session = new FakeSessionSecretStore(operations);
        var service = new SecretApplicationService(persistent, session);

        var result = await service.SetAsync(
            new SecretSetRequest("mail.imap.auth-code", "session-secret-value", SecretRetention.Session),
            CancellationToken.None);

        operations.Should().ContainInOrder(
            "persistent.delete:mail.imap.auth-code",
            "session.save:mail.imap.auth-code");
        persistent.Values.Should().NotContainKey("mail.imap.auth-code");
        session.Values["mail.imap.auth-code"].Should().Be("session-secret-value");
        result.Persistent.Should().BeFalse();
        JsonSerializer.Serialize(result).Should().NotContain("session-secret-value");
    }

    [Theory]
    [InlineData("arbitrary.secret")]
    [InlineData("")]
    public async Task Set_rejects_unrecognized_secret_names_without_writing(string name)
    {
        var operations = new List<string>();
        var service = new SecretApplicationService(
            new FakePersistentSecretStore(operations),
            new FakeSessionSecretStore(operations));

        var act = () => service.SetAsync(
            new SecretSetRequest(name, "secret-value", SecretRetention.Session),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        operations.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_rejects_empty_value_without_writing()
    {
        var operations = new List<string>();
        var service = new SecretApplicationService(
            new FakePersistentSecretStore(operations),
            new FakeSessionSecretStore(operations));

        var act = () => service.SetAsync(
            new SecretSetRequest("deepseek.api-key", "  ", SecretRetention.Session),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        operations.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_rejects_unknown_retention_value()
    {
        var operations = new List<string>();
        var service = new SecretApplicationService(
            new FakePersistentSecretStore(operations),
            new FakeSessionSecretStore(operations));

        var act = () => service.SetAsync(
            new SecretSetRequest("deepseek.api-key", "secret-value", (SecretRetention)99),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        operations.Should().BeEmpty();
    }

    private sealed class FakePersistentSecretStore(List<string> operations) : IPersistentSecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task SaveAsync(string name, string value, CancellationToken cancellationToken)
        {
            operations.Add($"persistent.save:{name}");
            Values[name] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult(Values.GetValueOrDefault(name));

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            operations.Add($"persistent.delete:{name}");
            Values.Remove(name);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessionSecretStore(List<string> operations) : ISessionSecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task SaveAsync(string name, string value, CancellationToken cancellationToken)
        {
            operations.Add($"session.save:{name}");
            Values[name] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult(Values.GetValueOrDefault(name));

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            operations.Add($"session.delete:{name}");
            Values.Remove(name);
            return Task.CompletedTask;
        }
    }
}