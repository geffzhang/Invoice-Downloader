using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Infrastructure;
using InvoiceFlowAI.Infrastructure.Url.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.UrlRecovery.Worker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--job-manifest") return 64;

        try
        {
            var services = new ServiceCollection();
            services.AddUrlRecoveryStrategies();
            using var provider = services.BuildServiceProvider();
            var host = new WorkerHost(provider.GetRequiredService<IUrlRecoveryClient>(), new UrlRecoveryWorkerManifestStore());
            return await host.RunAsync(args[1], CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return 70;
        }
    }
}