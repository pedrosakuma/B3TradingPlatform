namespace B3.Trading.Application;

/// <summary>
/// Static symbol → SecurityId map bound from
/// <c>Trading:SymbolDirectory:SecurityIds</c>. The trader UI submits
/// orders by symbol because that is what end-clients understand;
/// B3 wire (BinaryEntryPoint / SBE) addresses instruments by their
/// numeric SecurityId. Without a directory, every UI submit is
/// rejected with <c>securityId is required</c>.
/// </summary>
/// <remarks>
/// Resolution rules (see <see cref="OrdersEndpoints.MapPost"/> in
/// <c>B3.Trading.Api</c>):
/// <list type="number">
///   <item>If the request payload carries a non-zero
///   <c>SecurityId</c>, that wins (preserves the conformance suite
///   contract — explicit values are never overridden).</item>
///   <item>Otherwise the directory is consulted by symbol; case
///   does not matter (<see cref="StringComparer.OrdinalIgnoreCase"/>).</item>
///   <item>If neither path yields an id, the endpoint still returns
///   a 400 with the same message. The directory is additive, not a
///   silent fallback.</item>
/// </list>
/// The directory is intentionally simple in v1 (in-process, read at
/// startup). When the participant on-boards instruments dynamically
/// (e.g. via a real B3 Security Definition feed), a hot-reload or
/// service-backed implementation will replace this class behind the
/// same <see cref="TryResolve(string, out ulong)"/> API.
/// </remarks>
public sealed class SymbolDirectory
{
    private readonly IReadOnlyDictionary<string, ulong> _byName;

    public SymbolDirectory(SymbolDirectoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Always copy to enforce case-insensitive comparison even if
        // the binder produced a culture-sensitive dictionary.
        var copy = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in options.SecurityIds)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == 0) continue;
            copy[kv.Key] = kv.Value;
        }
        _byName = copy;
    }

    public int Count => _byName.Count;

    public bool TryResolve(string? symbol, out ulong securityId)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            securityId = 0;
            return false;
        }
        return _byName.TryGetValue(symbol, out securityId);
    }
}

/// <summary>
/// Bound from <c>Trading:SymbolDirectory</c>.
/// </summary>
public sealed class SymbolDirectoryOptions
{
    public const string SectionName = "Trading:SymbolDirectory";

    /// <summary>
    /// Symbol → SecurityId. Symbols with a zero or empty SecurityId
    /// are dropped at construction time (see <see cref="SymbolDirectory"/>).
    /// </summary>
    public Dictionary<string, ulong> SecurityIds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
