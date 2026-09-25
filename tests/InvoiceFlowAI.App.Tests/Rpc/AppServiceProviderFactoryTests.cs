using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class AppServiceProviderFactoryTests
{
    [Fact]
    public async Task Create_migrates_database_bootstraps_default_settings_and_registers_rpc_services()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-app-{Guid.NewGuid():N}");
        try
        {
            await using (var provider = AppServiceProviderFactory.Create(appDataDirectory))
            await using (var scope = provider.CreateAsyncScope())
            {
                var settings = await scope.ServiceProvider.GetRequiredService<IUserSettingsStore>()
                    .LoadAsync(CancellationToken.None);

                settings.RuleSetId.Should().Be("default");
                settings.RuleSetVersion.Should().Be(1);
                settings.Revision.Should().BeGreaterThan(0);
                (await scope.ServiceProvider.GetRequiredService<InvoiceFlowAI.Infrastructure.Persistence.InvoiceFlowDbContext>()
                    .Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            }
        }
        finally
        {
            if (Directory.Exists(appDataDirectory))
            {
                Directory.Delete(appDataDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Settings_slice_dispatches_through_scopes_without_exposing_secrets_or_run_methods()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-rpc-{Guid.NewGuid():N}");
        try
        {
            await using (var provider = AppServiceProviderFactory.Create(appDataDirectory))
            {
                var dispatcher = AppRpcComposition.CreateDispatcher(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    "3.0.0");

                var settings = await DispatchAsync(dispatcher, "settings.get", parameters: null);
                settings.GetProperty("revision").GetInt32().Should().Be(1);

                var updatedSettings = await DispatchAsync(dispatcher, "settings.update",
                    new SettingsUpdateRequest(1, CompanyName: "RPC Buyer"));
                updatedSettings.GetProperty("companyName").GetString().Should().Be("RPC Buyer");

                var accounts = await DispatchAsync(dispatcher, "account.list", parameters: null);
                accounts.GetProperty("items").GetArrayLength().Should().Be(0);

                var savedAccount = await DispatchAsync(dispatcher, "account.save",
                    new AccountSaveRequest(new MailboxAccountDraft(
                        "rpc-account",
                        "buyer@example.com",
                        "imap.example.com",
                        993,
                        true,
                        "mail.imap.auth-code",
                        "Buyer"), ExpectedRevision: 0));
                savedAccount.GetProperty("credentialConfigured").GetBoolean().Should().BeFalse();

                var accountTest = await DispatchAsync(dispatcher, "account.test", new AccountTestRequest("rpc-account"));
                accountTest.GetProperty("failureCode").GetString().Should().Be("CREDENTIALS_NOT_CONFIGURED");

                var providerTest = await DispatchAsync(dispatcher, "provider.test",
                    new ProviderTestRequest("deepseek", "deepseek.api-key"));
                providerTest.GetProperty("failureCode").GetString().Should().Be("CREDENTIALS_NOT_CONFIGURED");

                const string secretValue = "temporary-rpc-secret";
                var secretSet = await DispatchAsync(dispatcher, "secret.set",
                    new SecretSetRequest("deepseek.api-key", secretValue, SecretRetention.Session));
                secretSet.GetRawText().Should().NotContain(secretValue);
                (await DispatchAsync(dispatcher, "secret.delete", new SecretDeleteRequest("deepseek.api-key")))
                    .GetProperty("configured").GetBoolean().Should().BeFalse();

                var directory = await DispatchAsync(dispatcher, "directory.choose", parameters: null);
                directory.GetProperty("cancelled").GetBoolean().Should().BeTrue();
                dispatcher.RegisteredMethods.Should().NotContain("run.start");
            }
        }
        finally
        {
            if (Directory.Exists(appDataDirectory))
            {
                Directory.Delete(appDataDirectory, recursive: true);
            }
        }
    }

    private static async Task<JsonElement> DispatchAsync(RpcDispatcher dispatcher, string method, object? parameters)
    {
        JsonElement? element = parameters is null
            ? null
            : JsonSerializer.SerializeToElement(parameters, JsonOptions.Default);
        var response = await dispatcher.DispatchAsync(
            new RpcRequest<JsonElement?>(RpcDispatcher.Protocol, Guid.NewGuid().ToString("N"), method, element),
            CancellationToken.None);
        response.Ok.Should().BeTrue(response.Error?.UserMessage);
        return response.Result!.Value;
    }
}