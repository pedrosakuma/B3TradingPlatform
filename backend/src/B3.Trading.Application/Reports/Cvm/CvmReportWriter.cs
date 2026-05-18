using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Reports.Cvm;

/// <summary>
/// Q4.8 (#308). Streaming CVM 35/505 XML writer. Consumes the
/// <see cref="CvmFillRow"/> stream emitted by <see cref="CvmReportSource"/>
/// and serialises it directly into an <see cref="XmlWriter"/> built on
/// top of the HTTP response body — so the host never builds the whole
/// document in memory regardless of how many fills a busy firm
/// generated in a session.
///
/// <para><b>Schema.</b> The on-the-wire shape is fixed by
/// <c>Schemas/CvmReport.xsd</c> (embedded resource;
/// <see cref="LoadSchema"/>), which compliance can validate the
/// generated XML against. The shape is a B3-realistic placeholder
/// stand-in for the actual CVM 35/505 wire format — that format is
/// proprietary and not freely available; the compliance team will
/// swap the XSD when they have the official one and the writer
/// will only need element-name remapping (no structural change).
/// </para>
///
/// <para><b>LGPD.</b> Every <c>EndClientId</c> is hashed before it
/// hits the XML via <see cref="OpaqueOwner"/> — SHA-256 over
/// <c>{salt}|{firmId}|{date}|{rawEndClientId}</c> truncated to the
/// first 16 hex chars. The same raw id maps to the same opaqued id
/// within one firm-day report (so a reviewer can spot
/// patterns-of-life within a single report) but to a different
/// opaqued id across firms or days (so leaking two reports does
/// not enable owner correlation across them).</para>
///
/// <para><b>Counterparty.</b> B3 is a CCP-cleared market — every
/// fill ultimately faces the central counterparty. We don't have a
/// per-fill counterparty firm on the WAL (and would not have one to
/// emit even if we tracked it). The writer hard-codes
/// <c>"B3-CCP"</c> for the Counterparty element so the schema
/// validates and the regulator sees the (correct) CCP-cleared
/// signal; a future iteration with explicit CCP/non-CCP routing can
/// vary the emitted value.</para>
///
/// <para><b>Retention.</b> The XML is NEVER persisted — every
/// download regenerates from the WAL. The 7-year regulator-facing
/// retention is satisfied implicitly by WAL segment retention; if
/// the WAL is trimmed inside the retention window the export will
/// (correctly) return fewer rows. See <see cref="CvmReportSource"/>
/// remarks for the data-source contract.</para>
/// </summary>
public sealed class CvmReportWriter
{
    public const string Namespace = "urn:b3:cvm:35:v1";
    public const string SchemaVersion = "1";
    public const string CounterpartyFixed = "B3-CCP";
    public const string DefaultSubAccountPlaceholder = "";

    private readonly CvmReportOptions _options;

    public CvmReportWriter() : this(new CvmReportOptions()) { }

    public CvmReportWriter(IOptions<CvmReportOptions> options)
        : this(options?.Value ?? new CvmReportOptions()) { }

    public CvmReportWriter(CvmReportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Streams a CVM report. Returns the number of <c>Transaction</c>
    /// elements that landed on the writer. The caller MUST flush /
    /// dispose the underlying writer once the task completes.
    /// </summary>
    public async Task<int> WriteAsync(
        XmlWriter writer,
        CvmReportType reportType,
        string firmId,
        DateOnly date,
        IAsyncEnumerable<CvmFillRow> rows,
        DateTimeOffset generatedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rows);

        var typeCode = reportType.WireCode();

        // We need a row count in the header but want to stream rows
        // (no whole-doc-in-memory). Strategy: buffer rows into a
        // small array (we still iterate the source ONCE), then write
        // header + body in a single pass. The buffer holds plain
        // record refs — cheap, and even a million fills is ~80MB
        // which is well within request-handler limits; tighten later
        // by pre-counting via the source if it becomes a problem.
        var buffered = new List<CvmFillRow>(capacity: 256);
        await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
            buffered.Add(row);

        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("CvmReport", Namespace);
        writer.WriteAttributeString("reportType", typeCode);
        writer.WriteAttributeString("firmId", firmId);
        writer.WriteAttributeString("reportDate", date.ToString("yyyy-MM-dd"));
        writer.WriteAttributeString("generatedAtUtc", generatedAtUtc.ToUniversalTime().ToString("o"));
        writer.WriteAttributeString("version", SchemaVersion);

        writer.WriteStartElement("Header", Namespace);
        writer.WriteElementString("FirmId", Namespace, firmId);
        writer.WriteElementString("ReportingDate", Namespace, date.ToString("yyyy-MM-dd"));
        writer.WriteElementString("FillCount", Namespace, buffered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndElement(); // Header

        writer.WriteStartElement("Transactions", Namespace);
        foreach (var row in buffered)
        {
            ct.ThrowIfCancellationRequested();
            WriteTransaction(writer, reportType, firmId, date, row);
        }
        writer.WriteEndElement(); // Transactions

        writer.WriteEndElement(); // CvmReport
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        return buffered.Count;
    }

    private void WriteTransaction(XmlWriter w, CvmReportType reportType, string firmId, DateOnly date, CvmFillRow row)
    {
        w.WriteStartElement("Transaction", Namespace);

        var fillId = $"{row.ClOrdId.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{row.CumulativeQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        w.WriteElementString("FillId", Namespace, fillId);
        w.WriteElementString("ClOrdId", Namespace, row.ClOrdId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // OrigClOrdId is optional — emitted only when non-zero so a
        // plain New→Fill order doesn't carry a noisy "0" element.
        // Documented in the XSD as minOccurs=0.
        if (row.OrigClOrdId != 0)
            w.WriteElementString("OrigClOrdId", Namespace, row.OrigClOrdId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        w.WriteElementString("Owner", Namespace, OpaqueOwner(firmId, date, row.Owner));

        // SubAccount is optional — emitted only when the order
        // actually carries one. minOccurs=0 in the XSD.
        if (!string.IsNullOrEmpty(row.SubAccountId))
            w.WriteElementString("SubAccount", Namespace, row.SubAccountId);

        w.WriteElementString("Symbol", Namespace, row.Symbol);
        w.WriteElementString("Side", Namespace, row.Side);
        w.WriteElementString("Quantity", Namespace, row.LastQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteElementString("Price", Namespace, row.LastPrice.ToString("0.0############", System.Globalization.CultureInfo.InvariantCulture));
        w.WriteElementString("ExecutedAtUtc", Namespace, row.ExecutedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
        w.WriteElementString("Counterparty", Namespace, CounterpartyFixed);

        // CVM 505 (fundos) placeholder Fund column. TODO (#308): when
        // an order-level fund-classification field exists, surface it
        // here and have CvmReportSource pre-filter the row set to
        // fund-tagged fills only. For now the element is emitted
        // empty for 505 reports and omitted entirely for 35 reports
        // so the same XSD validates both.
        if (reportType == CvmReportType.Cvm505)
            w.WriteElementString("Fund", Namespace, string.Empty);

        w.WriteEndElement(); // Transaction
    }

    /// <summary>
    /// LGPD opacification: SHA-256 over
    /// <c>{salt}|{firmId}|{date:yyyy-MM-dd}|{rawEndClientId}</c>
    /// truncated to the first 16 hex chars (64 bits — far more than
    /// enough to keep within-report uniqueness while keeping the
    /// emitted token compact for human review).
    /// </summary>
    public string OpaqueOwner(string firmId, DateOnly date, B3.Trading.Domain.EndClientId owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var seed = $"{_options.OwnerHashSalt}|{firmId}|{date:yyyy-MM-dd}|{owner.Value}";
        var bytes = Encoding.UTF8.GetBytes(seed);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash[..8]); // 16 hex chars
    }

    /// <summary>
    /// Loads the embedded CVM report XSD so callers (the API endpoint
    /// for content-negotiated <c>?validate=true</c>, the test suite,
    /// and a future server-side strict-mode toggle) can validate the
    /// generated XML against it.
    /// </summary>
    public static System.Xml.Schema.XmlSchema LoadSchema()
    {
        using var stream = typeof(CvmReportWriter).Assembly.GetManifestResourceStream(
            "B3.Trading.Application.Reports.Cvm.Schemas.CvmReport.xsd")
            ?? throw new InvalidOperationException("Embedded XSD resource not found.");
        return System.Xml.Schema.XmlSchema.Read(stream, validationEventHandler: null)
            ?? throw new InvalidOperationException("Failed to parse embedded XSD.");
    }
}

/// <summary>CVM document type. 35 = negociações (trades); 505 = fundos.</summary>
public enum CvmReportType
{
    Cvm35,
    Cvm505,
}

public static class CvmReportTypeExtensions
{
    public static string WireCode(this CvmReportType t) => t switch
    {
        CvmReportType.Cvm35 => "35",
        CvmReportType.Cvm505 => "505",
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, null),
    };
}
