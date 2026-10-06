using System.Globalization;

namespace Sentinel.Infrastructure.Audit;

internal sealed record AuditChainBreak(long Sequence, string Reason);

/// <summary>
/// The rules one tenant's chain must satisfy, applied entry by entry in <c>(Sequence, Id)</c> order: positions run
/// 1..n without gaps or duplicates, every entry references the hash of its predecessor, and every stored hash matches
/// the entry's canonical form. Reasons are fixed texts with sequence numbers only, never column contents.
/// </summary>
internal sealed class AuditChainVerifier
{
    private long _expectedSequence = 1;
    private string? _previousHash;

    /// <summary>Entries examined so far, including the one at which a break was found.</summary>
    public long EntriesChecked { get; private set; }

    /// <returns>The first rule <paramref name="record"/> breaks, or <c>null</c> when it extends the chain.</returns>
    public AuditChainBreak? Check(AuditEntryRecord record)
    {
        EntriesChecked++;

        if (record.Sequence != _expectedSequence)
        {
            if (record.Sequence > _expectedSequence)
            {
                return Break(_expectedSequence, $"Sequence {_expectedSequence} is missing; the next entry has sequence {record.Sequence}.");
            }

            return _expectedSequence > 1 && record.Sequence == _expectedSequence - 1
                ? Break(record.Sequence, $"Sequence {record.Sequence} appears more than once.")
                : Break(record.Sequence, $"Sequence {record.Sequence} is not a valid chain position.");
        }

        if (!string.Equals(record.PreviousHash, _previousHash, StringComparison.Ordinal))
        {
            return _previousHash is null
                ? Break(record.Sequence, $"The first entry must not reference a previous hash.")
                : Break(record.Sequence, $"Entry {record.Sequence} does not reference the hash of entry {record.Sequence - 1}.");
        }

        if (!string.Equals(AuditCanonicalForm.ComputeHash(record), record.Hash, StringComparison.Ordinal))
        {
            return Break(record.Sequence, $"The stored hash of entry {record.Sequence} does not match its content.");
        }

        _expectedSequence++;
        _previousHash = record.Hash;
        return null;
    }

    private static AuditChainBreak Break(long sequence, FormattableString reason) =>
        new(sequence, reason.ToString(CultureInfo.InvariantCulture));
}
