using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Audit;

/// <summary>
/// #435 Part B. Default <see cref="IClOrdIdMasker"/> implementation.
/// Pattern lifted from <c>CvmReportWriter.OpaqueOwner</c>: SHA-256 over
/// <c>{salt}|{firmId}|{utcDate:yyyy-MM-dd}|{domain}|{id}</c>, truncated
/// to 16 hex chars (64 bits — more than enough to keep within-stream
/// uniqueness while staying compact for human review of drop-copy logs).
/// </summary>
public sealed class ClOrdIdMasker : IClOrdIdMasker
{
    private const string ClOrdIdDomain = "clordid";
    private const string AlgoIdDomain = "algoid";

    private readonly ClOrdIdMaskerOptions _options;
    private readonly Func<DateTime> _utcNow;

    public ClOrdIdMasker(IOptions<ClOrdIdMaskerOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), () => DateTime.UtcNow)
    { }

    internal ClOrdIdMasker(ClOrdIdMaskerOptions options, Func<DateTime> utcNow)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        if (string.IsNullOrEmpty(_options.ClOrdIdMaskSalt))
        {
            throw new InvalidOperationException(
                "ClOrdIdMaskerOptions.ClOrdIdMaskSalt must be set " +
                "(use ClOrdIdMaskerOptions.TestOnlySalt in tests). " +
                "Required for drop-copy compliance gating (#435 Part B).");
        }
    }

    public string MaskClOrdId(string firmId, ulong clOrdId) =>
        Compute(firmId, ClOrdIdDomain, clOrdId);

    public string MaskAlgoId(string firmId, ulong algoId) =>
        Compute(firmId, AlgoIdDomain, algoId);

    private string Compute(string firmId, string domain, ulong id)
    {
        var today = DateOnly.FromDateTime(_utcNow());
        var seed = $"{_options.ClOrdIdMaskSalt}|{firmId}|{today:yyyy-MM-dd}|{domain}|{id}";
        var bytes = Encoding.UTF8.GetBytes(seed);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash[..8]);
    }
}
