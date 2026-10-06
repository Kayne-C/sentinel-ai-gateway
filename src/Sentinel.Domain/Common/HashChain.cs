using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Domain.Common;

/// <summary>
/// Tamper evidence for append-only logs: every entry's hash covers the previous entry's hash, so editing or
/// deleting any entry breaks every hash after it.
/// </summary>
public static class HashChain
{
    /// <returns>Lower-case hex SHA-256 over the previous hash and the fields, newline-separated.</returns>
    public static string Compute(string? previousHash, params ReadOnlySpan<string?> fields)
    {
        var builder = new StringBuilder(previousHash ?? string.Empty);
        foreach (var field in fields)
        {
            builder.Append('\n').Append(field ?? string.Empty);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
