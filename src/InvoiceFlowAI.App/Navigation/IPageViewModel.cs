namespace InvoiceFlowAI.App.Navigation;

public interface IPageViewModel
{
    string Key { get; }
    string Title { get; }
    string Eyebrow { get; }
    Task OnActivatedAsync(CancellationToken cancellationToken);
    Task OnDeactivatedAsync(CancellationToken cancellationToken);
}
