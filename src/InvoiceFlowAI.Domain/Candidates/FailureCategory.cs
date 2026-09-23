namespace InvoiceFlowAI.Domain.Candidates;

public enum FailureCategory
{
    Input = 0,
    Document = 1,
    Network = 2,
    Authentication = 3,
    Quota = 4,
    Persistence = 5,
    Cancellation = 6,
    Validation = 7,
    Internal = 8,
}