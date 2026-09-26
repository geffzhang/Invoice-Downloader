using System.Security.Cryptography;
using System.Text.Json;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public abstract class TypedRpcHandler<TParams, TResult>(string method, bool allowNullParams = false) : IRpcHandler
    where TParams : class
{
    public string Method { get; } = method;

    public async Task<RpcHandlerResult> HandleAsync(
        RpcRequest<JsonElement?> request,
        CancellationToken cancellationToken)
    {
        TParams parameters;
        if (request.Params is null || request.Params.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (!allowNullParams)
            {
                return Failure(RpcDispatcher.InvalidParamsCode, "Invalid request parameters.");
            }

            parameters = null!;
        }
        else
        {
            try
            {
                parameters = request.Params.Value.Deserialize<TParams>(JsonOptions.Default)
                    ?? throw new JsonException("Request parameters are required.");
            }
            catch (JsonException)
            {
                return Failure(RpcDispatcher.InvalidParamsCode, "Invalid request parameters.");
            }
        }

        try
        {
            var result = await ExecuteAsync(parameters, cancellationToken).ConfigureAwait(false);
            return new RpcHandlerResult(JsonSerializer.SerializeToElement(result, JsonOptions.Default), null);
        }
        catch (SettingsRevisionConflictException exception)
        {
            var details = JsonSerializer.SerializeToElement(new
            {
                expectedRevision = exception.ExpectedRevision,
                actualRevision = exception.ActualRevision,
            }, JsonOptions.Default);
            return Failure(RpcErrorCodes.SettingsRevisionConflict, "Settings changed. Reload and try again.", details);
        }
        catch (MailboxAccountRevisionConflictException)
        {
            return Failure(RpcErrorCodes.MailboxAccountRevisionConflict, "Mailbox account changed. Reload and try again.");
        }
        catch (InvoiceFlowAI.Application.Runs.DesktopRunException exception)
        {
            return Failure(exception.ErrorCode, exception.Message);
        }
        catch (CryptographicException)
        {
            return Failure(RpcErrorCodes.CredentialsNotConfigured, "Credentials are unavailable. Enter them again.");
        }
        catch (ArgumentException)
        {
            return Failure(RpcDispatcher.InvalidParamsCode, "Invalid request parameters.");
        }
    }

    protected abstract Task<TResult> ExecuteAsync(TParams parameters, CancellationToken cancellationToken);

    protected static RpcHandlerResult Failure(string code, string message, JsonElement? details = null)
        => new(null, new RpcError(code, "rpc", false, message, details is not null, details));
}

public sealed record RpcEmptyParams;