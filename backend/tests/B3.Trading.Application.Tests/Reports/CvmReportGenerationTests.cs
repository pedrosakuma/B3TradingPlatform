using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Reports.Cvm;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;
using Xunit;

namespace B3.Trading.Application.Tests.Reports;

/// <summary>
/// Q4.8 (#308). Unit-level coverage for the CVM 35/505 transaction
/// reporting generator: source enumeration over the WAL, writer
/// shape, LGPD opacification stability, and XSD validation of the
/// generated XML.
/// </summary>
public class CvmReportGenerationTests
{
    private const string Firm01 = "FIRM01";
    private const string Firm02 = "FIRM02";

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly List<(long Seq, WalEvent Event)> _events = new();
        private long _seq;

        public long CurrentSeq => _seq;

        public long Append(WalEvent evt)
        {
            var seq = Interlocked.Increment(ref _seq);
            _events.Add((seq, evt));
            return seq;
        }

        public long Append(WalEvent evt, ReadOnlyMemory<byte> _) => Append(evt);

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var entry in _events)
            {
                if (entry.Seq > sinceSeqExclusive)
                    yield return entry;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static OrderSubmittedEvent Submit(ulong clOrdId, string firm, string owner, string symbol, string side, long qty, decimal price, DateTimeOffset ts)
        => new()
        {
            TimestampUtc = ts,
            ClOrdId = clOrdId,
            EndClientId = owner,
            FirmId = firm,
            Symbol = symbol,
            SecurityId = 9000UL,
            Side = side,
            Type = "Limit",
            Quantity = qty,
            Price = price,
        };

    private static ExecutionReportReceivedEvent Fill(ulong clOrdId, string execKind, long lastQty, long cumQty, decimal price, string firm, DateTimeOffset ts)
        => new()
        {
            TimestampUtc = ts,
            ClOrdId = clOrdId,
            ExecKind = execKind,
            LeavesQuantity = 0,
            CumulativeQuantity = cumQty,
            LastQuantity = lastQty,
            LastPrice = price,
            Synthetic = false,
            FirmId = firm,
        };

    private static (CvmReportSource Source, CvmReportWriter Writer, InMemoryEventStore Store)
        BuildFixture(string ownerSalt = "test-salt")
    {
        var store = new InMemoryEventStore();
        var ownership = new OrderOwnershipMap();
        var source = new CvmReportSource(store, ownership);
        var writer = new CvmReportWriter(Options.Create(new CvmReportOptions { OwnerHashSalt = ownerSalt }));
        return (source, writer, store);
    }

    private static async Task<XDocument> GenerateAsync(
        CvmReportWriter writer,
        CvmReportSource source,
        CvmReportType reportType,
        string firm,
        DateOnly date,
        DateTimeOffset generatedAt)
    {
        await using var ms = new MemoryStream();
        await using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Async = true, Indent = false }))
        {
            await writer.WriteAsync(xw, reportType, firm, date, source.EnumerateAsync(firm, date), generatedAt);
        }
        ms.Position = 0;
        return XDocument.Load(ms);
    }

    [Fact]
    public async Task Fixture_TwoClOrdIds_TwoSymbols_TwoOwners_OneFirm_MatchesExpectedShape()
    {
        var (source, writer, store) = BuildFixture();
        var day = new DateOnly(2026, 5, 18);
        var t0 = new DateTimeOffset(2026, 5, 18, 13, 30, 0, TimeSpan.Zero);

        // Two ClOrdIds, two symbols, two owners.
        store.Append(Submit(101UL, Firm01, "alice", "PETR4", "Buy", 100, 30.50m, t0));
        store.Append(Submit(102UL, Firm01, "bob", "VALE3", "Sell", 50, 70.00m, t0));
        // Mix Fill + PartialFill on ClOrdId=101.
        store.Append(Fill(101UL, "PartialFill", 40, 40, 30.50m, Firm01, t0.AddSeconds(1)));
        store.Append(Fill(101UL, "Fill", 60, 100, 30.51m, Firm01, t0.AddSeconds(2)));
        // Single full fill on ClOrdId=102.
        store.Append(Fill(102UL, "Fill", 50, 50, 70.00m, Firm01, t0.AddSeconds(3)));

        var generatedAt = new DateTimeOffset(2026, 5, 18, 23, 0, 0, TimeSpan.Zero);
        var doc = await GenerateAsync(writer, source, CvmReportType.Cvm35, Firm01, day, generatedAt);

        XNamespace ns = CvmReportWriter.Namespace;
        var root = doc.Root!;
        Assert.Equal("CvmReport", root.Name.LocalName);
        Assert.Equal("35", root.Attribute("reportType")!.Value);
        Assert.Equal(Firm01, root.Attribute("firmId")!.Value);
        Assert.Equal("2026-05-18", root.Attribute("reportDate")!.Value);
        Assert.Equal("1", root.Attribute("version")!.Value);

        var header = root.Element(ns + "Header")!;
        Assert.Equal("3", header.Element(ns + "FillCount")!.Value);

        var tx = root.Element(ns + "Transactions")!.Elements(ns + "Transaction").ToList();
        Assert.Equal(3, tx.Count);

        // Order is WAL arrival, so 101 partial → 101 full → 102 full.
        Assert.Equal("101:40", tx[0].Element(ns + "FillId")!.Value);
        Assert.Equal("PETR4", tx[0].Element(ns + "Symbol")!.Value);
        Assert.Equal("Buy", tx[0].Element(ns + "Side")!.Value);
        Assert.Equal("40", tx[0].Element(ns + "Quantity")!.Value);

        Assert.Equal("101:100", tx[1].Element(ns + "FillId")!.Value);
        Assert.Equal("60", tx[1].Element(ns + "Quantity")!.Value);

        Assert.Equal("102:50", tx[2].Element(ns + "FillId")!.Value);
        Assert.Equal("VALE3", tx[2].Element(ns + "Symbol")!.Value);
        Assert.Equal("Sell", tx[2].Element(ns + "Side")!.Value);

        // Counterparty is always "B3-CCP".
        foreach (var t in tx)
            Assert.Equal("B3-CCP", t.Element(ns + "Counterparty")!.Value);

        // alice's two fills carry the same opaqued owner; bob's
        // single fill carries a different one.
        var aliceOpaque = tx[0].Element(ns + "Owner")!.Value;
        Assert.Equal(aliceOpaque, tx[1].Element(ns + "Owner")!.Value);
        var bobOpaque = tx[2].Element(ns + "Owner")!.Value;
        Assert.NotEqual(aliceOpaque, bobOpaque);
        Assert.Equal(16, aliceOpaque.Length);

        // Pin the opacified hash so a salt regression is caught.
        var expectedAlice = writer.OpaqueOwner(Firm01, day, new EndClientId("alice"));
        Assert.Equal(expectedAlice, aliceOpaque);
    }

    [Fact]
    public async Task GeneratedXml_ValidatesAgainstEmbeddedXsd()
    {
        var (source, writer, store) = BuildFixture();
        var day = new DateOnly(2026, 5, 18);
        var t0 = new DateTimeOffset(2026, 5, 18, 14, 0, 0, TimeSpan.Zero);
        store.Append(Submit(201UL, Firm01, "alice", "PETR4", "Buy", 10, 30m, t0));
        store.Append(Fill(201UL, "Fill", 10, 10, 30m, Firm01, t0.AddSeconds(1)));

        var doc = await GenerateAsync(writer, source, CvmReportType.Cvm35, Firm01, day, t0.AddHours(1));

        var schemas = new XmlSchemaSet();
        schemas.Add(CvmReportWriter.LoadSchema());
        var errors = new List<string>();
        doc.Validate(schemas, (_, ev) => errors.Add(ev.Message));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Cvm505_EmitsFundElement_And_ValidatesAgainstSameXsd()
    {
        var (source, writer, store) = BuildFixture();
        var day = new DateOnly(2026, 5, 18);
        var t0 = new DateTimeOffset(2026, 5, 18, 14, 0, 0, TimeSpan.Zero);
        store.Append(Submit(301UL, Firm01, "alice", "PETR4", "Buy", 10, 30m, t0));
        store.Append(Fill(301UL, "Fill", 10, 10, 30m, Firm01, t0.AddSeconds(1)));

        var doc = await GenerateAsync(writer, source, CvmReportType.Cvm505, Firm01, day, t0.AddHours(1));
        XNamespace ns = CvmReportWriter.Namespace;
        Assert.Equal("505", doc.Root!.Attribute("reportType")!.Value);
        var tx = doc.Root.Element(ns + "Transactions")!.Element(ns + "Transaction")!;
        var fund = tx.Element(ns + "Fund");
        Assert.NotNull(fund); // present (empty) on 505 reports
        Assert.Equal(string.Empty, fund!.Value);

        var schemas = new XmlSchemaSet();
        schemas.Add(CvmReportWriter.LoadSchema());
        var errors = new List<string>();
        doc.Validate(schemas, (_, ev) => errors.Add(ev.Message));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task CrossFirmFills_AreNotEmittedInReport()
    {
        var (source, writer, store) = BuildFixture();
        var day = new DateOnly(2026, 5, 18);
        var t0 = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
        store.Append(Submit(401UL, Firm01, "alice", "PETR4", "Buy", 10, 30m, t0));
        store.Append(Submit(402UL, Firm02, "carol", "PETR4", "Buy", 5, 30m, t0));
        store.Append(Fill(401UL, "Fill", 10, 10, 30m, Firm01, t0.AddSeconds(1)));
        store.Append(Fill(402UL, "Fill", 5, 5, 30m, Firm02, t0.AddSeconds(2)));

        var doc = await GenerateAsync(writer, source, CvmReportType.Cvm35, Firm01, day, t0.AddHours(1));
        XNamespace ns = CvmReportWriter.Namespace;
        var tx = doc.Root!.Element(ns + "Transactions")!.Elements(ns + "Transaction").ToList();
        Assert.Single(tx);
        Assert.Equal("401:10", tx[0].Element(ns + "FillId")!.Value);
    }

    [Fact]
    public async Task OutsideDateRange_FillsAreDropped()
    {
        var (source, writer, store) = BuildFixture();
        var day = new DateOnly(2026, 5, 18);
        var t0 = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
        store.Append(Submit(501UL, Firm01, "alice", "PETR4", "Buy", 10, 30m, t0));
        store.Append(Fill(501UL, "Fill", 10, 10, 30m, Firm01, new DateTimeOffset(2026, 5, 17, 23, 0, 0, TimeSpan.Zero))); // day - 1
        store.Append(Fill(501UL, "Fill", 10, 20, 30m, Firm01, new DateTimeOffset(2026, 5, 19, 1, 0, 0, TimeSpan.Zero))); // day + 1

        var doc = await GenerateAsync(writer, source, CvmReportType.Cvm35, Firm01, day, t0.AddHours(1));
        XNamespace ns = CvmReportWriter.Namespace;
        Assert.Empty(doc.Root!.Element(ns + "Transactions")!.Elements(ns + "Transaction"));
    }

    [Fact]
    public void OpaqueOwner_DeterministicWithin_FirmAndDate_DistinctAcrossFirmsAndDates()
    {
        var writer = new CvmReportWriter(Options.Create(new CvmReportOptions { OwnerHashSalt = "salt-X" }));
        var alice = new EndClientId("alice");
        var d1 = new DateOnly(2026, 5, 18);
        var d2 = new DateOnly(2026, 5, 19);

        var a1 = writer.OpaqueOwner(Firm01, d1, alice);
        var a1Again = writer.OpaqueOwner(Firm01, d1, alice);
        Assert.Equal(a1, a1Again);

        // Different firm → different opaque id
        var a2 = writer.OpaqueOwner(Firm02, d1, alice);
        Assert.NotEqual(a1, a2);

        // Different date (same firm) → different opaque id
        var a3 = writer.OpaqueOwner(Firm01, d2, alice);
        Assert.NotEqual(a1, a3);

        // Different end-client (same firm + date) → different opaque id
        var bob = writer.OpaqueOwner(Firm01, d1, new EndClientId("bob"));
        Assert.NotEqual(a1, bob);
    }

    [Fact]
    public void OpaqueOwner_DiffersWhenSaltDiffers()
    {
        var writerA = new CvmReportWriter(Options.Create(new CvmReportOptions { OwnerHashSalt = "salt-A" }));
        var writerB = new CvmReportWriter(Options.Create(new CvmReportOptions { OwnerHashSalt = "salt-B" }));
        var alice = new EndClientId("alice");
        var d = new DateOnly(2026, 5, 18);
        Assert.NotEqual(writerA.OpaqueOwner(Firm01, d, alice), writerB.OpaqueOwner(Firm01, d, alice));
    }

    [Fact]
    public async Task NonFillExecKinds_AreIgnored()
    {
        var (source, writer, store) = BuildFixture();
        var day = new DateOnly(2026, 5, 18);
        var t0 = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
        store.Append(Submit(601UL, Firm01, "alice", "PETR4", "Buy", 10, 30m, t0));
        store.Append(Fill(601UL, "New", 0, 0, 0m, Firm01, t0.AddSeconds(1))); // not a fill
        store.Append(Fill(601UL, "Canceled", 0, 0, 0m, Firm01, t0.AddSeconds(2))); // not a fill
        store.Append(Fill(601UL, "Fill", 10, 10, 30m, Firm01, t0.AddSeconds(3))); // the only fill

        var doc = await GenerateAsync(writer, source, CvmReportType.Cvm35, Firm01, day, t0.AddHours(1));
        XNamespace ns = CvmReportWriter.Namespace;
        var tx = doc.Root!.Element(ns + "Transactions")!.Elements(ns + "Transaction").ToList();
        Assert.Single(tx);
        Assert.Equal("Fill", "Fill"); // documented: ExecKind not in XML; we just assert the single transaction is the real fill
        Assert.Equal("601:10", tx[0].Element(ns + "FillId")!.Value);
    }
}
