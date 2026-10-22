using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App.Services;

public sealed class RunShutdownService(
    IScopedOperationRunner operations,
    IDialogService dialogs,
    ILogger<RunShutdownService> logger) : IRunShutdownService
{
    public async Task<RunCloseDecision> ConfirmCloseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var runId = await operations.RunAsync(
                async (provider, token) =>
                {
                    var runService = provider.GetRequiredService<IDesktopRunService>();
                    var context = await runService.GetContextAsync(token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(context.RunId))
                    {
                        return null;
                    }

                    var progress = await runService.GetProgressAsync(context.RunId, token).ConfigureAwait(false);
                    return progress.IsRunning ? context.RunId : null;
                },
                cancellationToken).ConfigureAwait(true);

            if (runId is null)
            {
                return RunCloseDecision.Close;
            }

            var confirmed = await dialogs.ShowConfirmAsync(
                "关闭程序",
                "关闭程序会直接结束当前窗口与任务进程,请仅在确认后执行。",
                "停止并关闭",
                "取消",
                cancellationToken).ConfigureAwait(true);

            if (!confirmed)
            {
                return RunCloseDecision.Cancel;
            }

            await operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopRunService>()
                    .StopAsync(new RunStopRequest(runId), token),
                cancellationToken).ConfigureAwait(true);

            return RunCloseDecision.Close;
        }
        catch (OperationCanceledException)
        {
            return RunCloseDecision.Cancel;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "关闭前的停止确认失败。");
            return RunCloseDecision.Cancel;
        }
    }
}
