namespace InvoiceFlowAI.Domain.Candidates;

/// <summary>
/// Opaque, stable identifier for a document candidate. Composed from the
/// source mailbox UID, attachment filename hash and, for URL candidates, the
/// verified URL host hash. The value is opaque to the UI and to logs.
/// </summary>
public readonly record struct DocumentIdentity(string Value)
{
    public static DocumentIdentity Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Document identity must be non-empty.", nameof(value));
        }
        return new DocumentIdentity(value);
    }

    public override string ToString() => Value;
}