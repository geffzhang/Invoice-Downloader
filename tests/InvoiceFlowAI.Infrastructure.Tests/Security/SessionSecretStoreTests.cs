using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Security;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Security;

public sealed class SessionSecretStoreTests
{
    [Fact]
    public async Task Session_values_are_visible_only_to_the_store_instance_that_received_them()
    {
        var firstSession = new SessionSecretStore();
        await firstSession.SaveAsync("mail.imap.auth-code", "process-secret", CancellationToken.None);

        var loaded = await firstSession.GetAsync("mail.imap.auth-code", CancellationToken.None);
        var secondSessionValue = await new SessionSecretStore().GetAsync("mail.imap.auth-code", CancellationToken.None);

        loaded.Should().Be("process-secret");
        secondSessionValue.Should().BeNull();
    }

    [Fact]
    public async Task Composite_reads_session_before_persistent_and_delete_clears_both()
    {
        var persistent = new FakePersistentSecretStore();
        var session = new SessionSecretStore();
        await persistent.SaveAsync("deepseek.api-key", "persistent-secret", CancellationToken.None);
        await session.SaveAsync("deepseek.api-key", "session-secret", CancellationToken.None);
        var composite = new CompositeSecretStore(persistent, session);

        (await composite.GetAsync("deepseek.api-key", CancellationToken.None)).Should().Be("session-secret");
        await composite.DeleteAsync("deepseek.api-key", CancellationToken.None);
        (await persistent.GetAsync("deepseek.api-key", CancellationToken.None)).Should().BeNull();
        (await session.GetAsync("deepseek.api-key", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Composite_falls_back_to_persistent_when_session_has_no_value()
    {
        var persistent = new FakePersistentSecretStore();
        await persistent.SaveAsync("deepseek.api-key", "persisted-value", CancellationToken.None);
        var composite = new CompositeSecretStore(persistent, new SessionSecretStore());

        (await composite.GetAsync("deepseek.api-key", CancellationToken.None)).Should().Be("persisted-value");
    }

    private sealed class FakePersistentSecretStore : IPersistentSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task SaveAsync(string name, string value, CancellationToken cancellationToken)
        {
            _values[name] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult(_values.GetValueOrDefault(name));

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }
}