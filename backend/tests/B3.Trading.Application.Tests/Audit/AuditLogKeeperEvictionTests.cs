using System;
using System.Linq;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Options;
using Xunit;

namespace B3.Trading.Application.Tests.Audit;

/// <summary>
/// Pass-2 review (#322) P2: an earlier <c>List&lt;T&gt;.RemoveAt(0)</c>
/// implementation of the bounded ring was O(capacity) per evicted
/// entry, turning the recovery pre-pass into O(N · capacity) once the
/// WAL audit-event count exceeded the cap. The keeper now uses a
/// head-indexed circular buffer so eviction is O(1).
/// </summary>
public class AuditLogKeeperEvictionTests
{
    [Fact]
    public void FullRingEviction_KeepsNewestCapacityEntries_NewestFirst()
    {
        var keeper = new AuditLogKeeper(Options.Create(new AuditLogOptions { Capacity = 4 }));
        var baseUtc = new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.Zero);

        for (long seq = 1; seq <= 10; seq++)
        {
            keeper.Apply(seq, new AuditLogEvent
            {
                EventType = AuditEventTypes.AuthLoginSuccess,
                Outcome = AuditOutcomes.Success,
                TimestampUtc = baseUtc.AddSeconds(seq),
                ActorUsername = $"u{seq}",
            });
        }

        Assert.Equal(4, keeper.Count);
        var page = keeper.Query(
            since: baseUtc.AddYears(-1),
            until: baseUtc.AddYears(1),
            user: null, typePattern: null, outcome: null,
            limit: 100, cursorSeq: null);

        Assert.Equal(new long[] { 10, 9, 8, 7 }, page.Entries.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void RepeatedWrap_PreservesCircularInvariants()
    {
        var keeper = new AuditLogKeeper(Options.Create(new AuditLogOptions { Capacity = 3 }));
        var baseUtc = new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.Zero);

        for (long seq = 1; seq <= 9; seq++)
        {
            keeper.Apply(seq, new AuditLogEvent
            {
                EventType = AuditEventTypes.AdminConfigChange,
                Outcome = AuditOutcomes.Success,
                TimestampUtc = baseUtc.AddSeconds(seq),
            });
        }

        Assert.Equal(3, keeper.Count);
        var page = keeper.Query(baseUtc.AddYears(-1), baseUtc.AddYears(1), null, null, null, 100, null);
        Assert.Equal(new long[] { 9, 8, 7 }, page.Entries.Select(e => e.Seq).ToArray());
    }
}
