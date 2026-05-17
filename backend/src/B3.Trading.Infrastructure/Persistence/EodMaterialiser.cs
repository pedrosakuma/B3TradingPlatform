using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Options;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Materialises the day-segmented WAL into a single self-describing
/// <c>eod-{date}.json</c> summary used for end-of-day reconciliation.
/// Reads the day's segments with a fresh <see cref="SegmentReader"/>
/// (independent of the live writer) so it can run while the platform
/// keeps trading the next session.
///
/// <para>
/// Comparison against an exchange-side EOD report is intentionally a
/// future hook — the current EntryPoint stub does not expose one. The
/// summary itself is enough for self-audit ("what did we send/receive
/// today?") and for diff'ing against a manually-supplied EP report.
/// </para>
/// </summary>
public sealed class EodMaterialiser : IEodMaterialiser
{
    public bool IsAvailable => true;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly PersistenceOptions _opts;

    public EodMaterialiser(IOptions<PersistenceOptions> opts) : this(opts.Value) { }
    public EodMaterialiser(PersistenceOptions opts) => _opts = opts;

    public EodReport Materialise(DateOnly date)
    {
        var dayDir = Path.Combine(_opts.DataDirectory, _opts.FirmId, "wal",
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var report = new EodReport
        {
            Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FirmId = _opts.FirmId,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };

        if (!Directory.Exists(dayDir)) return report;

        using var sha = SHA256.Create();
        foreach (var logFile in Directory.EnumerateFiles(dayDir, "*.log").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var reader = new SegmentReader(logFile);
            foreach (var payload in reader.ReadAll())
            {
                report.RecordCount++;
                sha.TransformBlock(payload, 0, payload.Length, null, 0);
                if (!FileEventStore.TryDeserialize(payload, out var evt, out _))
                {
                    // Pass-2 review (#296) P1-B. Unknown discriminator
                    // (newer engine wrote this WAL). EOD counts the
                    // record + SHA bytes — both are kind-agnostic —
                    // but doesn't classify it into any per-kind bucket.
                    continue;
                }
                switch (evt)
                {
                    case OrderSubmittedEvent: report.OrderSubmittedCount++; break;
                    case ExecutionReportReceivedEvent er:
                        report.ExecutionReportCount++;
                        if (er.ExecKind.Equals("Fill", StringComparison.OrdinalIgnoreCase))
                            report.FilledCount++;
                        else if (er.ExecKind.Equals("PartialFill", StringComparison.OrdinalIgnoreCase))
                            report.PartialFillCount++;
                        else if (er.ExecKind.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
                            report.CanceledCount++;
                        else if (er.ExecKind.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
                            report.RejectedCount++;
                        break;
                    case KillSwitchToggledEvent: report.KillSwitchToggleCount++; break;
                    case SymbolHaltToggledEvent: report.SymbolHaltToggleCount++; break;
                    case SessionPhaseChangedEvent: report.SessionPhaseChangeCount++; break;
                }
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        report.Sha256 = Convert.ToHexString(sha.Hash!);

        var eodDir = Path.Combine(_opts.DataDirectory, _opts.FirmId, "eod");
        Directory.CreateDirectory(eodDir);
        var path = Path.Combine(eodDir, $"eod-{report.Date}.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions));
        report.Path = path;
        return report;
    }
}

/// <summary>
/// Drop-in <see cref="IEodMaterialiser"/> registered when persistence is
/// disabled. <see cref="IsAvailable"/> is <c>false</c>, and
/// <see cref="Materialise"/> throws so any caller that bypasses the
/// availability check fails loudly instead of producing an empty report.
/// </summary>
public sealed class DisabledEodMaterialiser : IEodMaterialiser
{
    public bool IsAvailable => false;

    public EodReport Materialise(DateOnly date) =>
        throw new InvalidOperationException(
            "EOD materialisation is unavailable: persistence is disabled.");
}
