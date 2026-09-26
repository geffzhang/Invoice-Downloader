namespace InvoiceFlowAI.Application.Extraction;

public sealed class AiAuthenticationFailureGate
{
    private int _blocked;

    public bool IsBlocked => Volatile.Read(ref _blocked) != 0;

    public bool TryBlock() => Interlocked.Exchange(ref _blocked, 1) == 0;
}
