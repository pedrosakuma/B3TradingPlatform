using B3.Trading.Domain;

namespace B3.Trading.Application.SubAccount;

/// <summary>
/// Default <see cref="ISubAccountWireIdMapper"/> implementation. Hashes
/// <c>firmId + '\0' + subAccountId.Value</c> through FNV-1a 32-bit and
/// folds the (extremely rare) zero output to <c>1</c>, guaranteeing
/// a non-zero <c>uint</c> for every non-null input.
///
/// <para>
/// <b>Determinism contract.</b> The output is a pure function of the
/// pair (firmId, subAccountId.Value) with no I/O, no clock, no
/// allocation beyond the UTF-8 encoding of those two strings. Same
/// inputs always produce the same output across processes, hosts and
/// restarts — by design, since the same trader's orders must carry the
/// same wire id across an entire trading session.
/// </para>
///
/// <para>
/// <b>Collision posture.</b> A firm's sub-account namespace is small
/// (handful to low-hundreds of distinct ids in realistic deployments),
/// so 2^32 output space is well above the birthday bound — collisions
/// are not expected at this scale. If a registered lookup-table mapper
/// is ever introduced (operator-negotiated registry with the broker),
/// it can be swapped in via DI without changing the gateway. The seam
/// in <see cref="ISubAccountWireIdMapper"/> exists exactly for that
/// migration path.
/// </para>
///
/// <para>
/// <b>Encoding.</b> UTF-8 of the firmId, a single zero byte separator
/// (cannot appear inside <see cref="SubAccountId.Value"/> nor inside
/// the firmId per <c>FirmConfigValidation</c>), then UTF-8 of the
/// sub-account id. The separator is what makes
/// <c>("FIRM01", "A.B")</c> distinguishable from <c>("FIRM01A", ".B")</c>
/// instead of degenerating to the same byte stream.
/// </para>
/// </summary>
public sealed class DeterministicSubAccountWireIdMapper : ISubAccountWireIdMapper
{
    private const uint FnvOffsetBasis = 2166136261u;
    private const uint FnvPrime = 16777619u;

    public uint? TryMap(string firmId, SubAccountId? subAccountId)
    {
        if (subAccountId is null) return null;
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);

        var hash = FnvOffsetBasis;
        hash = HashUtf8(hash, firmId);
        hash = HashByte(hash, 0);
        hash = HashUtf8(hash, subAccountId.Value);

        // Reserve 0 → 1. Zero is a valid uint but several downstream
        // consumers (logs, dashboards, JSON projections) treat "0" as
        // an "unset" sentinel; folding to 1 keeps the wire output
        // unambiguously meaningful while leaving the rest of the
        // 2^32 - 1 space intact.
        return hash == 0u ? 1u : hash;
    }

    private static uint HashUtf8(uint hash, string s)
    {
        // Pure ASCII fast path — SubAccountId enforces [A-Za-z0-9._-]
        // (single-byte UTF-8 for every legal codepoint), so a char-by-char
        // pass produces the same byte sequence System.Text.Encoding.UTF8
        // would, without allocating a temporary byte[]. FirmId validation
        // (FirmConfigValidation) likewise restricts to ASCII id chars.
        foreach (var c in s)
            hash = HashByte(hash, (byte)c);
        return hash;
    }

    private static uint HashByte(uint hash, byte b) => (hash ^ b) * FnvPrime;
}
