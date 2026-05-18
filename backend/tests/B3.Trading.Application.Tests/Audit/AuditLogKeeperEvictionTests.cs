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

    /// <summary>
    /// Pass-3 review (#322): pre-cap geometric growth must reset
    /// <c>_head</c> after the underlying array doubles. The previous
    /// implementation left <c>_head</c> at its wrapped value (0) after
    /// the first growth at <c>len=1024</c>, so the 1025th append
    /// overwrote slot 0 and left slots <c>1024..len-1</c> null —
    /// <see cref="AuditLogKeeper.Query"/> then dereferenced a null
    /// entry and crashed with NRE. Capacity 2048 forces at least one
    /// growth from the initial 1024-slot ring while staying well
    /// below the cap.
    /// </summary>
    [Fact]
    public void GeometricGrowthPastInitialRingLength_DoesNotLoseEntriesOrCrashQuery()
    {
        var keeper = new AuditLogKeeper(Options.Create(new AuditLogOptions { Capacity = 2048 }));
        var baseUtc = new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.Zero);

        const int total = 1100;
        for (long seq = 1; seq <= total; seq++)
        {
            keeper.Apply(seq, new AuditLogEvent
            {
                EventType = AuditEventTypes.AuthLoginSuccess,
                Outcome = AuditOutcomes.Success,
                TimestampUtc = baseUtc.AddSeconds(seq),
            });
        }

        Assert.Equal(total, keeper.Count);

        var page = keeper.Query(baseUtc.AddYears(-1), baseUtc.AddYears(1), null, null, null, limit: 500, cursorSeq: null);
        Assert.Equal(500, page.Entries.Count);
        // Newest-first; no NRE; no gaps.
        for (var i = 0; i < page.Entries.Count; i++)
        {
            Assert.Equal(total - i, page.Entries[i].Seq);
        }
    }
}
