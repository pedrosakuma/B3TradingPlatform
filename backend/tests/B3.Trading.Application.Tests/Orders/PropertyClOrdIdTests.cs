using System.Collections.Concurrent;
using B3.Trading.Application;
using B3.Trading.Domain;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace B3.Trading.Application.Tests.Orders;

/// <summary>
/// RFC §4.4 — ClOrdId monotonicity per owner under concurrent
/// allocation. <see cref="ClOrdIdPrefixRegistry.Generate"/> assigns a
/// packed <c>ulong</c> per call; the per-owner counter (bottom 40 bits)
/// is advanced via <see cref="System.Threading.Interlocked.Increment"/>,
/// so under arbitrarily concurrent calls every owner observes a
/// contiguous, unique counter range <c>[1..N(owner)]</c>. We additionally
/// pin that <see cref="ClOrdIdPrefixRegistry.AdvanceCounterTo"/> (the
/// WAL-replay watermark advance, RFC §4.4 wording "no double-issue
/// after recovery") never lets a subsequent live <see cref="ClOrdIdPrefixRegistry.Generate"/>
/// re-issue an id already observed.
///
/// <para>The generator emits a randomised (owners × workers × per-worker)
/// concurrent workload and a synthetic "reconnect watermark" per owner
/// that simulates WAL replay before live traffic resumes. Determinism:
/// FsCheck seeded; counter monotonicity is a hardware-level property
/// of <c>Interlocked</c> so the only flake risk would be a real bug.
/// FsCheck prints the seed on failure.</para>
/// </summary>
[Properties(
    Arbitrary = new[] { typeof(PropertyClOrdIdTests.ClOrdIdGenerators) },
    MaxTest = 100,
    QuietOnSuccess = true)]
public class PropertyClOrdIdTests
{
    /// <summary>
    /// §4.4 — for any (owners × workers × perWorker) concurrent workload,
    /// each owner's allocated ids decompose into:
    /// <list type="bullet">
    ///   <item>a single shared <c>prefixIdx</c></item>
    ///   <item>a perfect-bijection counter set <c>{1..workers·perWorker}</c></item>
    /// </list>
    /// — i.e. no gaps, no duplicates, and no overflow into another
    /// owner's prefix space.
    /// </summary>
    [Property(DisplayName = "§4.4 per-owner ClOrdIds form contiguous [1..N] under concurrent Generate")]
    public Property ClOrdId_PerOwner_StrictlyIncreasing_UnderConcurrency(ClOrdIdWorkload workload)
    {
        var registry = new ClOrdIdPrefixRegistry();
        var perOwner = RunConcurrentGenerate(registry, workload);

        var allOk = true;
        var labels = new List<string>();
        var perOwnerPrefix = new Dictionary<string, ulong>();
        foreach (var (owner, ids) in perOwner)
        {
            var prefixes = ids.Select(id => id >> ClOrdIdPrefixRegistry.CounterBits).Distinct().ToList();
            var counters = ids.Select(id => (long)(id & ClOrdIdPrefixRegistry.CounterMask)).ToList();
            var expectedN = workload.Workers * workload.PerWorker;
            var prefixUnique = prefixes.Count == 1;
            var noZeroCounter = !counters.Contains(0);
            var uniqueCounters = counters.Distinct().Count() == expectedN;
            var rangeContiguous = counters.OrderBy(c => c).SequenceEqual(Enumerable.Range(1, expectedN).Select(i => (long)i));
            var ok = prefixUnique && noZeroCounter && uniqueCounters && rangeContiguous;
            if (!ok)
                labels.Add($"owner={owner}: prefixes={prefixes.Count}; counters_unique={counters.Distinct().Count()}/{expectedN}; min={counters.Min()}; max={counters.Max()}");
            if (prefixUnique) perOwnerPrefix[owner] = prefixes[0];
            allOk &= ok;
        }

        // Cross-owner invariants. Per-owner contiguous counters alone
        // do not rule out a registry bug that hands every owner the
        // same prefix (e.g. AllocatePrefix racing on a non-atomic
        // _nextPrefix increment) — that would silently produce
        // identical ClOrdIds across owners. The §4.4 contract is
        // platform-wide uniqueness; check it explicitly. gpt-5.5 review
        // (Nov 2025).
        var prefixesAcrossOwners = perOwnerPrefix.Values.ToList();
        var allPrefixesDistinct = prefixesAcrossOwners.Distinct().Count() == perOwner.Count;
        var allIdsGlobally = perOwner.Values.SelectMany(v => v).ToList();
        var globallyUnique = allIdsGlobally.Count == allIdsGlobally.Distinct().Count();
        allOk &= allPrefixesDistinct && globallyUnique;
        if (!allPrefixesDistinct)
            labels.Add($"prefix collision across owners: {prefixesAcrossOwners.Count - prefixesAcrossOwners.Distinct().Count()} dupes");
        if (!globallyUnique)
            labels.Add($"global ClOrdId collision: {allIdsGlobally.Count - allIdsGlobally.Distinct().Count()} dupes");

        return allOk.Label(string.Join("; ", labels.DefaultIfEmpty($"owners={workload.Owners}, workers={workload.Workers}, perWorker={workload.PerWorker}")));
    }

    /// <summary>
    /// §4.4 — replay watermark contract. The recovery path is:
    /// generate ids in a "live" registry, persist them via WAL, then on
    /// crash open a <b>fresh</b> registry and feed the observed ids
    /// through <see cref="ClOrdIdPrefixRegistry.AdvanceCounterTo"/>
    /// (this is exactly what
    /// <c>EventReplayer.Apply(OrderSubmittedEvent)</c> does at §4.4 of
    /// the RFC). A live <see cref="ClOrdIdPrefixRegistry.Generate"/>
    /// after recovery must not re-issue any id already observed in the
    /// WAL.
    ///
    /// <para>The fresh registry — distinct from the one that produced
    /// the pre-replay ids — is the only configuration that genuinely
    /// exercises <c>AdvanceCounterTo</c>: when the same registry
    /// instance is reused, the counter watermark has already advanced
    /// past the observed ids by virtue of <c>Generate</c> itself, and
    /// the property would pass even if <c>AdvanceCounterTo</c> were a
    /// total no-op (gpt-5.5 review, Nov 2025).</para>
    /// </summary>
    [Property(DisplayName = "§4.4 AdvanceCounterTo never causes a future Generate to re-issue an observed id")]
    public Property ClOrdId_AdvanceCounterTo_PreventsDoubleIssueAfterReplay(ClOrdIdReplayScenario scenario)
    {
        var owner = new EndClientId("alice");

        // Phase 1: "live" registry produces a workload that the host
        // would have persisted to the WAL before crashing.
        var live = new ClOrdIdPrefixRegistry();
        var observed = new HashSet<ulong>();
        for (var i = 0; i < scenario.PreReplayGenerates; i++)
            observed.Add(live.Generate(owner));

        // Phase 2: a *fresh* registry (the post-crash process) replays
        // the observed ids via AdvanceCounterTo in random order — which
        // matches WAL recovery semantics where every persisted
        // OrderSubmittedEvent feeds AdvanceCounterTo (see
        // EventReplayer.Apply). Random order also exercises the
        // monotonic-max contract: AdvanceCounterTo must take the max
        // and ignore lower values arriving later.
        var recovered = new ClOrdIdPrefixRegistry();
        var rng = new Random(scenario.Seed);
        foreach (var id in observed.OrderBy(_ => rng.Next()))
            recovered.AdvanceCounterTo(owner, id);

        // Phase 3: live traffic resumes against the recovered registry.
        // No new id may collide with anything observed pre-crash.
        var postReplay = new List<ulong>();
        for (var i = 0; i < scenario.PostReplayGenerates; i++)
            postReplay.Add(recovered.Generate(owner));

        var collided = postReplay.Where(observed.Contains).ToList();
        return (collided.Count == 0)
            .Label($"pre={scenario.PreReplayGenerates}, post={scenario.PostReplayGenerates}, collisions={collided.Count}");
    }

    /// <summary>
    /// RFC stress floor: 16 owners × 16 workers × 64 per-worker
    /// concurrent allocations. Pinned as a deterministic
    /// <see cref="FactAttribute"/> so a regression in
    /// <c>Interlocked</c> usage surfaces on every CI run even if no
    /// property draw lands on this corner. Runtime &lt; 1s.
    /// </summary>
    [Fact(DisplayName = "§4.4 stress: 16 owners × 16 workers × 64 allocs; contiguous [1..1024] per owner")]
    public void Stress_ClOrdId_16Owners_16Workers_64PerWorker_PerfectBijection()
    {
        var workload = new ClOrdIdWorkload(Owners: 16, Workers: 16, PerWorker: 64);
        var registry = new ClOrdIdPrefixRegistry();
        var perOwner = RunConcurrentGenerate(registry, workload);

        Assert.Equal(workload.Owners, perOwner.Count);
        var expectedN = workload.Workers * workload.PerWorker;
        var allPrefixes = new List<ulong>();
        var allIds = new List<ulong>();
        foreach (var (owner, ids) in perOwner)
        {
            var prefixes = ids.Select(id => id >> ClOrdIdPrefixRegistry.CounterBits).Distinct().ToList();
            Assert.Single(prefixes);
            allPrefixes.Add(prefixes[0]);
            allIds.AddRange(ids);
            var counters = ids.Select(id => (long)(id & ClOrdIdPrefixRegistry.CounterMask)).OrderBy(c => c).ToList();
            Assert.Equal(Enumerable.Range(1, expectedN).Select(i => (long)i), counters);
        }
        // Cross-owner prefixes and global ClOrdIds must be unique.
        Assert.Equal(workload.Owners, allPrefixes.Distinct().Count());
        Assert.Equal(allIds.Count, allIds.Distinct().Count());
    }

    // ---- harness ---------------------------------------------------------

    private static Dictionary<string, List<ulong>> RunConcurrentGenerate(
        ClOrdIdPrefixRegistry registry, ClOrdIdWorkload w)
    {
        var perOwner = new ConcurrentDictionary<string, ConcurrentBag<ulong>>();
        var owners = Enumerable.Range(0, w.Owners).Select(i => new EndClientId($"owner{i}")).ToArray();
        foreach (var o in owners) perOwner[o.Value] = new ConcurrentBag<ulong>();

        // workers × owners threads — every owner is hit by every worker
        // so the registry sees max-pressure interleaving for each
        // counter. Using Thread (not Task) so we don't depend on the
        // thread-pool's queuing heuristics for determinism.
        var threads = new Thread[w.Workers * w.Owners];
        using var start = new ManualResetEventSlim(initialState: false);
        var idx = 0;
        for (var t = 0; t < w.Workers; t++)
        {
            foreach (var o in owners)
            {
                var localOwner = o;
                threads[idx++] = new Thread(() =>
                {
                    start.Wait();
                    for (var i = 0; i < w.PerWorker; i++)
                        perOwner[localOwner.Value].Add(registry.Generate(localOwner));
                });
            }
        }
        foreach (var th in threads) th.Start();
        start.Set();
        foreach (var th in threads) th.Join();

        return perOwner.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
    }

    // ---- generators ------------------------------------------------------

    public sealed record ClOrdIdWorkload(int Owners, int Workers, int PerWorker);
    public sealed record ClOrdIdReplayScenario(int PreReplayGenerates,
        int PostReplayGenerates, int Seed);

    public static class ClOrdIdGenerators
    {
        // Owners (1..6), workers (1..8), perWorker (1..40).
        // Worst case: 6×8×40 = 1920 allocations per property draw — well
        // under the 2^40 counter ceiling and runs in <50ms per draw.
        public static Arbitrary<ClOrdIdWorkload> Workload() =>
            Arb.From(
                from o in Gen.Choose(1, 6)
                from w in Gen.Choose(1, 8)
                from p in Gen.Choose(1, 40)
                select new ClOrdIdWorkload(o, w, p),
                ShrinkWorkload);

        // PreReplay (1..50); post (1..50).
        public static Arbitrary<ClOrdIdReplayScenario> Replay() =>
            Arb.From(
                from pre in Gen.Choose(1, 50)
                from post in Gen.Choose(1, 50)
                from seed in Gen.Choose(int.MinValue, int.MaxValue)
                select new ClOrdIdReplayScenario(pre, post, seed),
                ShrinkReplay);

        private static IEnumerable<ClOrdIdWorkload> ShrinkWorkload(ClOrdIdWorkload w)
        {
            if (w.Owners > 1) yield return w with { Owners = w.Owners / 2 };
            if (w.Workers > 1) yield return w with { Workers = w.Workers / 2 };
            if (w.PerWorker > 1) yield return w with { PerWorker = w.PerWorker / 2 };
        }

        private static IEnumerable<ClOrdIdReplayScenario> ShrinkReplay(ClOrdIdReplayScenario s)
        {
            if (s.PreReplayGenerates > 1)
                yield return s with { PreReplayGenerates = s.PreReplayGenerates / 2 };
            if (s.PostReplayGenerates > 1)
                yield return s with { PostReplayGenerates = s.PostReplayGenerates / 2 };
        }
    }
}
