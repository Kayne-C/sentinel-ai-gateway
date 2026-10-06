namespace Sentinel.Application.Knowledge;

/// <summary>
/// Thrown by the knowledge repository when a save lost a race (another writer stored a newer version, or created
/// the same external id first). Without this check two concurrent revisions could both "win" and the later one
/// would silently restore principals the earlier one had just removed.
/// </summary>
public sealed class KnowledgeConcurrencyException : Exception
{
    public KnowledgeConcurrencyException()
    {
    }

    public KnowledgeConcurrencyException(string message)
        : base(message)
    {
    }

    public KnowledgeConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
