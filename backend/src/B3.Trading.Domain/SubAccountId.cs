namespace B3.Trading.Domain;

/// <summary>
/// Q4.1 (#301). Sub-account identifier inside an end-client / trader.
/// A single <see cref="EndClientId"/> may carry N sub-accounts
/// (e.g. <c>tradingdesk</c>, <c>prop</c>, <c>clientA</c>) so that
/// orders, fills, positions, P&amp;L, and per-sub-account risk
/// buckets can be tracked separately while the master end-client
/// view continues to aggregate naturally.
///
/// <para>
/// <b>Nullability convention.</b> Every sub-account-aware field on
/// the public API (orders, snapshots, WAL events, risk options) is
/// modelled as <see cref="SubAccountId"/>? — <c>null</c> means
/// "master bucket / no sub-account specified", which is also the
/// only value legacy callers (pre-#301) ever supplied. This keeps
/// forward-compat with every persisted WAL segment and snapshot
/// already on disk: missing field deserialises as <c>null</c> ==
/// master bucket, exactly the semantic those rows actually carried.
/// </para>
///
/// <para>
/// <b>Identifier shape.</b> Case-sensitive string keyed verbatim
/// off the wire (POST /sub-accounts body). The constructor rejects
/// empty/whitespace and any token longer than 64 characters or
/// containing characters outside <c>[A-Za-z0-9._-]</c> so the id
/// can flow safely through JSON payloads, log messages, and metric
/// tags without escaping. Sub-accounts are namespaced per-firm at
/// the registry level (see <c>SubAccountsRegistry</c>), so the same
/// id under FIRM01 and FIRM02 are distinct addresses — this value
/// type carries only the leaf id.
/// </para>
/// </summary>
public sealed record SubAccountId
{
    /// <summary>Hard cap on the id length. Sized to fit comfortably
    /// in a single short JSON field without ballooning WAL rows or
    /// metric cardinality.</summary>
    public const int MaxLength = 64;

    public SubAccountId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("SubAccountId cannot be null or whitespace.", nameof(value));
        if (value.Length > MaxLength)
            throw new ArgumentException(
                $"SubAccountId length {value.Length} exceeds max {MaxLength}.",
                nameof(value));
        foreach (var c in value)
        {
            if (!IsValidChar(c))
                throw new ArgumentException(
                    $"SubAccountId '{value}' contains invalid character '{c}'; allowed: A-Z a-z 0-9 . _ -",
                    nameof(value));
        }
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static bool IsValidChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    /// <summary>
    /// Convenience parser used by REST/WAL replay paths: empty/null
    /// query-string arguments map to <c>null</c> (master bucket); a
    /// non-empty value is validated through the regular constructor.
    /// </summary>
    public static SubAccountId? FromNullableString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return new SubAccountId(raw);
    }
}
