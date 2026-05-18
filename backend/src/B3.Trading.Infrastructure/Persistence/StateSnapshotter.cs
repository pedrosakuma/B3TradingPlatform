using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Scheduling;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Captures and restores the platform's stateful Application-layer
/// components in a single round-trip. Snapshot capture must run under
/// the <see cref="EventDispatcher"/> lock to be consistent with the
/// recorded WAL seq; restore is single-threaded and runs at startup
/// before the host begins accepting requests.
/// </summary>
public sealed class StateSnapshotter
{
    private readonly WorkingOrderBook _orders;
    private readonly PositionKeeper _positions;
    private readonly KillSwitchService _killSwitch;
    private readonly SymbolHaltService _symbolHalts;
    private readonly SessionPhaseService _sessionPhases;
    private readonly ClOrdIdPrefixRegistry _clOrdIds;
    private readonly OrderOwnershipMap _ownership;
    private readonly AlgoBook _algos;
    private readonly AlgoIdRegistry _algoIds;
    private readonly CashLedger _cash;
    private readonly CashKeeper? _cashKeeper;
    private readonly FeeKeeper? _feeKeeper;
    private readonly PnlKeeper? _pnlKeeper;
    private readonly InMemoryUserBotCredentialRegistry? _userBotCredentials;
    private readonly InMemoryUserBotSessionRegistry? _userBotSessions;
    private readonly IUserBotOrderMappingRegistry? _userBotMappings;
    /// <summary>
    /// Pass-4 review (#255). Optional. When wired (production
    /// composition includes the GTD scheduler), <see cref="CaptureRaw"/>
    /// snapshots the scheduler's in-flight audited-expired set under
    /// the dispatcher lock and <see cref="Restore"/> re-marks every id
    /// before WAL replay begins, closing the snapshot-mid-fire window
    /// where an audit envelope was on disk at <c>seq &lt;= snap.Seq</c>
    /// but the order was still working in the snapshot.
    /// </summary>
    private readonly GtdExpirationScheduler? _gtdScheduler;
    /// <summary>
    /// Pass-1 review (#295) P1#1. Optional — when wired the snapshot
    /// pipeline captures per-POV scheduling progress so a restart can
    /// restore the cumulative-market-volume baseline. Null-tolerant for
    /// legacy test compositions that don't exercise POV.
    /// </summary>
    private readonly PovProgressBook? _povProgress;
    /// <summary>
    /// Pass-1 review (#296) P1-C. Optional — when wired the snapshot
    /// pipeline captures per-Pegged in-flight repeg-cycle markers so
    /// a restart can rebuild <c>AlgoParentRuntime.RepegPending</c> +
    /// the expected-cancel marker, preventing a post-restart
    /// cancel-ack from being misread as a venue-cancel.
    /// Null-tolerant for legacy test compositions that don't exercise
    /// Pegged.
    /// </summary>
    private readonly PeggedRepegBook? _peggedRepeg;
    /// <summary>
    /// Pass-6 review (#299) P1. Optional. When wired the snapshot
    /// pipeline captures every <see cref="PendingReplacementRegistry"/>
    /// entry (including the ambiguous-margin-held flag) so a
    /// snapshot taken AFTER an <c>OrderReplaceAmbiguousMarginHeldEvent</c>
    /// survives recovery whose WAL tail starts past that event. The
    /// restore path additionally re-invokes
    /// <see cref="IReplaceMarginCoordinator.PrepareReplaceAsync"/>
    /// (when <see cref="_replaceMargin"/> is wired) for every restored
    /// entry whose <c>AmbiguousMarginHeld</c> flag is set — mirroring
    /// the WAL-replay branch added in pass-5 so the post-restart sweep
    /// has both the registry entry and the held reservation.
    /// </summary>
    private readonly PendingReplacementRegistry? _replacements;
    private readonly IReplaceMarginCoordinator? _replaceMargin;
    private readonly SubAccountsRegistry? _subAccounts;
    private readonly SubAccountPositionKeeper? _subAccountPositions;
    private readonly SubAccountPnlKeeper? _subAccountPnl;

    public StateSnapshotter(
        WorkingOrderBook orders,
        PositionKeeper positions,
        KillSwitchService killSwitch,
        SymbolHaltService symbolHalts,
        SessionPhaseService sessionPhases,
        ClOrdIdPrefixRegistry clOrdIds,
        OrderOwnershipMap ownership,
        AlgoBook algos,
        AlgoIdRegistry algoIds,
        CashLedger cash,
        InMemoryUserBotCredentialRegistry? userBotCredentials = null,
        InMemoryUserBotSessionRegistry? userBotSessions = null,
        IUserBotOrderMappingRegistry? userBotMappings = null,
        GtdExpirationScheduler? gtdScheduler = null,
        CashKeeper? cashKeeper = null,
        FeeKeeper? feeKeeper = null,
        PnlKeeper? pnlKeeper = null,
        PovProgressBook? povProgress = null,
        PeggedRepegBook? peggedRepeg = null,
        PendingReplacementRegistry? replacements = null,
        IReplaceMarginCoordinator? replaceMargin = null,
        SubAccountsRegistry? subAccounts = null,
        SubAccountPositionKeeper? subAccountPositions = null,
        SubAccountPnlKeeper? subAccountPnl = null)
    {
        _orders = orders;
        _positions = positions;
        _killSwitch = killSwitch;
        _symbolHalts = symbolHalts;
        _sessionPhases = sessionPhases;
        _clOrdIds = clOrdIds;
        _ownership = ownership;
        _algos = algos;
        _algoIds = algoIds;
        _cash = cash;
        _cashKeeper = cashKeeper;
        _feeKeeper = feeKeeper;
        _pnlKeeper = pnlKeeper;
        _userBotCredentials = userBotCredentials;
        _userBotSessions = userBotSessions;
        _userBotMappings = userBotMappings;
        _gtdScheduler = gtdScheduler;
        _povProgress = povProgress;
        _peggedRepeg = peggedRepeg;
        _replacements = replacements;
        _replaceMargin = replaceMargin;
        _subAccounts = subAccounts;
        _subAccountPositions = subAccountPositions;
        _subAccountPnl = subAccountPnl;
    }

    public PlatformSnapshot Capture(long seq) => Project(CaptureRaw(seq));

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// described in RFC §5.8 / P6. Caller MUST hold
    /// <c>EventDispatcher.WithSnapshotLock</c>: every per-registry
    /// <c>RawSnapshot()</c> here is allowed to read mutable scalars off
    /// live aggregates (Order/Algo/Position) without further
    /// synchronisation, and the resulting raw arrays then feed the
    /// lock-free <see cref="Project"/> step.
    ///
    /// <para><b>Snapshot consistency invariant (RFC §4.3).</b> The raw
    /// arrays returned here are a stable point-in-time photograph of
    /// every captured aggregate. <see cref="OrderRaw"/> and
    /// <see cref="AlgoRaw"/> snapshot the mutable scalars
    /// (<c>Status</c>, <c>LeavesQuantity</c>, <c>FilledQuantity</c>,
    /// <c>TerminalReason</c>, …) by value during this call, so the
    /// projection step never re-reads them off the live aggregate after
    /// the dispatcher lock is released. No event with
    /// <c>seq &gt; <paramref name="seq"/></c> can leak into the
    /// projected <see cref="PlatformSnapshot"/>.</para>
    /// </summary>
    public RawPlatformSnapshot CaptureRaw(long seq) => new()
    {
        Seq = seq,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Orders = _orders.RawSnapshot(),
        Algos = _algos.RawSnapshot(),
        Positions = _positions.RawSnapshot(),
        KilledEndClients = _killSwitch.RawSnapshotKilledEndClients(),
        KilledFirms = _killSwitch.RawSnapshotKilledFirms(),
        HaltedSymbols = _symbolHalts.RawSnapshot(),
        DefaultPhase = _sessionPhases.DefaultPhase,
        SessionPhaseOverrides = _sessionPhases.RawSnapshotOverrides(),
        ClOrdIds = _clOrdIds.RawSnapshot(),
        AlgoIds = _algoIds.RawSnapshot(),
        Ownership = _ownership.RawSnapshot(),
        CashBalances = _cash.RawSnapshot(),
        CashByEndclient = _cashKeeper?.RawSnapshot() ?? Array.Empty<CashKeeperRaw>(),
        FeesByEndclientDay = _feeKeeper?.RawSnapshot() ?? Array.Empty<FeeKeeperRaw>(),
        FeeSeenExecutionIds = _feeKeeper?.RawSnapshotSeenIds() ?? Array.Empty<string>(),
        PnlRealizedByEndclientSymbolDay = _pnlKeeper?.RawSnapshotRealized() ?? Array.Empty<PnlRealizedRaw>(),
        PnlAvgCost = _pnlKeeper?.RawSnapshotAvgCost() ?? Array.Empty<PnlAvgCostRaw>(),
        PnlUnknownBasis = _pnlKeeper?.RawSnapshotUnknownBasis() ?? Array.Empty<PnlUnknownBasisRaw>(),
        PnlSeenExecutionIds = _pnlKeeper?.RawSnapshotSeenIds() ?? Array.Empty<string>(),
        UserBotCredentials = _userBotCredentials?.RawSnapshot() ?? Array.Empty<UserBotCredential>(),
        BotSessions = _userBotSessions?.RawSnapshot() ?? Array.Empty<BotSessionState>(),
        BotOrderMappings = _userBotMappings?.RawSnapshotOrders() ?? Array.Empty<BotOrderMappingRaw>(),
        BotCancelMappings = _userBotMappings?.RawSnapshotCancels() ?? Array.Empty<BotCancelMappingRaw>(),
        AuditedExpiredIds = _gtdScheduler?.SnapshotAuditedExpiredIds() ?? Array.Empty<ulong>(),
        PovProgress = _povProgress is null
            ? Array.Empty<PovProgressRaw>()
            : _povProgress.Snapshot()
                .Select(t => new PovProgressRaw(t.FirmId, t.AlgoId, t.Progress.MarketVolumeSeen, t.Progress.LastEvaluateAtUtc))
                .ToArray(),
        PeggedRepegPending = _peggedRepeg is null
            ? Array.Empty<PeggedRepegPendingRaw>()
            : _peggedRepeg.Snapshot()
                .Select(t => new PeggedRepegPendingRaw(t.FirmId, t.AlgoId,
                    t.Pending.CancelledChildClOrdId, t.Pending.TargetPrice, t.Pending.AtUtc))
                .ToArray(),
        PeggedRepegHistory = _peggedRepeg is null
            ? Array.Empty<PeggedRepegHistoryRaw>()
            : _peggedRepeg.SnapshotHistory()
                .Select(t => new PeggedRepegHistoryRaw(t.FirmId, t.AlgoId, t.ChildClOrdIds.ToArray(), t.EvictionLogged))
                .ToArray(),
        PendingReplacements = _replacements is null
            ? Array.Empty<PendingReplacementRaw>()
            : _replacements.Snapshot()
                .Select(s => new PendingReplacementRaw(
                    OriginalClOrdId: s.Intent.OriginalClOrdId,
                    NewClOrdId: s.Intent.NewClOrdId,
                    OwnerEndClientId: s.Intent.Owner.Value,
                    Symbol: s.Intent.Symbol,
                    SecurityId: s.Intent.SecurityId,
                    Side: s.Intent.Side,
                    Type: s.Intent.Type,
                    NewQuantity: s.Intent.NewQuantity,
                    NewPrice: s.Intent.NewPrice,
                    FirmId: s.Intent.FirmId,
                    ParentAlgoId: s.Intent.ParentAlgoId,
                    AlgoSliceSeq: s.Intent.AlgoSliceSeq,
                    RequestedTimeInForce: s.Intent.RequestedTimeInForce,
                    RequestedStopPrice: s.Intent.RequestedStopPrice,
                    RequestedGoodTillDate: s.Intent.RequestedGoodTillDate,
                    CreatedAtUtc: s.CreatedAt,
                    AmbiguousMarginHeld: s.AmbiguousMarginHeld,
                    AmbiguousAtUtc: s.AmbiguousAt,
                    NewRemainingNotional: s.NewRemainingNotional))
                .ToArray(),
        SubAccounts = _subAccounts?.Snapshot() ?? Array.Empty<SubAccountSnapshot>(),
        SubAccountPositions = _subAccountPositions?.Snapshot() ?? Array.Empty<SubAccountPositionSnapshot>(),
        SubAccountPnl = _subAccountPnl?.Snapshot() ?? Array.Empty<SubAccountPnlSnapshot>(),
        SubAccountPnlBasis = _subAccountPnl?.SnapshotBasis() ?? Array.Empty<SubAccountPnlBasisSnapshot>(),
    };

    /// <summary>
    /// Phase-2 projection for the two-phase snapshot pipeline (RFC §5.8 /
    /// P6). Consumes the lock-side <see cref="RawPlatformSnapshot"/> and
    /// produces the persisted <see cref="PlatformSnapshot"/> shape. All
    /// expensive work — enum→string formatting, <c>OrderBy</c> sorting,
    /// per-record DTO allocation, final <c>List&lt;T&gt;</c>
    /// materialisation — happens here, OUTSIDE the dispatcher lock.
    ///
    /// <para>Pure function of <paramref name="raw"/>: never reads the
    /// live aggregate maps, only the raw arrays captured under the
    /// dispatcher lock. Output is byte-equivalent to the legacy
    /// in-lock <see cref="Capture"/> path; the only ordering difference
    /// is that <see cref="WorkingOrderBook"/>, <see cref="AlgoBook"/>,
    /// and ownership lists are now sorted by id (deterministic and
    /// independent of <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
    /// enumeration order, which was already non-deterministic in the
    /// legacy code).</para>
    /// </summary>
    public static PlatformSnapshot Project(RawPlatformSnapshot raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var workingOrders = new List<OrderSnapshot>(raw.Orders.Length);
        for (var i = 0; i < raw.Orders.Length; i++)
        {
            var r = raw.Orders[i];
            var o = r.Order;
            workingOrders.Add(new OrderSnapshot(
                o.ClOrdId, o.Owner.Value, o.Symbol, o.SecurityId,
                o.Side.ToString(), o.Type.ToString(),
                o.Quantity, o.Price, r.Leaves, r.Cum,
                r.Status.ToString(), o.FirmId, o.ParentAlgoId, o.AlgoSliceSeq)
            {
                IsStale = r.IsStale,
                StaleReason = r.StaleReason,
                StaledAtUtc = r.StaledAtUtc,
                TimeInForce = o.TimeInForce.ToString(),
                StopPrice = o.StopPrice,
                GoodTillDate = o.GoodTillDate,
                DisplayQty = o.DisplayQty,
                DisplayResetPolicy = o.DisplayResetPolicy?.ToString(),
                SubAccountId = o.SubAccountId?.Value,
            });
        }

        var algos = new List<AlgoSnapshot>(raw.Algos.Length);
        for (var i = 0; i < raw.Algos.Length; i++)
            algos.Add(AlgoBook.ProjectRaw(raw.Algos[i]));

        var positions = new List<PositionSnapshot>(raw.Positions.Length);
        for (var i = 0; i < raw.Positions.Length; i++)
        {
            var p = raw.Positions[i];
            positions.Add(new PositionSnapshot(p.EndClientId, p.Symbol, p.NetQuantity, p.AverageEntryPrice, p.FirmId));
        }

        var ownership = new List<OwnershipMappingSnapshot>(raw.Ownership.Length);
        for (var i = 0; i < raw.Ownership.Length; i++)
            ownership.Add(new OwnershipMappingSnapshot(raw.Ownership[i].ClOrdId, raw.Ownership[i].EndClientId));

        var cash = new List<CashBalanceSnapshot>(raw.CashBalances.Length);
        for (var i = 0; i < raw.CashBalances.Length; i++)
            cash.Add(new CashBalanceSnapshot(raw.CashBalances[i].EndClientId, raw.CashBalances[i].Available));

        // Q2.2 (#269). Project the CashKeeper's raw rows into the
        // persisted dict shape. Dictionary (vs list) is mandated by the
        // spec; deterministic insertion order is not required (callers
        // index by end-client id).
        var cashByEndclient = new Dictionary<string, decimal>(raw.CashByEndclient.Length);
        for (var i = 0; i < raw.CashByEndclient.Length; i++)
            cashByEndclient[raw.CashByEndclient[i].EndClientId] = raw.CashByEndclient[i].Available;

        // Q2.3 (#270). FeeKeeper rows projected into the
        // <c>{endClientId}|{yyyy-MM-dd} → total</c> dict shape; same
        // shape rationale as cashByEndclient (no-deterministic order
        // required, callers index by composite key). Seen-set is a
        // flat list — order does not matter on restore (HashSet add).
        var feesByEndclientDay = new Dictionary<string, decimal>(raw.FeesByEndclientDay.Length);
        for (var i = 0; i < raw.FeesByEndclientDay.Length; i++)
        {
            var f = raw.FeesByEndclientDay[i];
            feesByEndclientDay[FeeKeeper.FormatKey(f.EndClientId, f.Day)] = f.Total;
        }
        var feeSeen = new List<string>(raw.FeeSeenExecutionIds.Length);
        for (var i = 0; i < raw.FeeSeenExecutionIds.Length; i++)
            feeSeen.Add(raw.FeeSeenExecutionIds[i]);

        // Q2.4 (#271). PnlKeeper rows projected into the persisted
        // shape. Same dict choice as fees — callers index by composite
        // key, no deterministic order required. Avg-cost basis is a
        // list (1:1 with positions) and the seen-set is a flat list.
        var pnlRealized = new Dictionary<string, decimal>(raw.PnlRealizedByEndclientSymbolDay.Length);
        for (var i = 0; i < raw.PnlRealizedByEndclientSymbolDay.Length; i++)
        {
            var p = raw.PnlRealizedByEndclientSymbolDay[i];
            pnlRealized[PnlKeeper.FormatRealizedKey(p.FirmId, p.EndClientId, p.Symbol, p.Day)] = p.Realized;
        }
        var pnlAvgCost = new List<PnlAvgCostSnapshot>(raw.PnlAvgCost.Length);
        for (var i = 0; i < raw.PnlAvgCost.Length; i++)
        {
            var a = raw.PnlAvgCost[i];
            pnlAvgCost.Add(new PnlAvgCostSnapshot(a.EndClientId, a.Symbol, a.NetQuantity, a.AvgPrice, a.FirmId));
        }
        var pnlUnknownBasis = new List<PnlUnknownBasisSnapshot>(raw.PnlUnknownBasis.Length);
        for (var i = 0; i < raw.PnlUnknownBasis.Length; i++)
        {
            var u = raw.PnlUnknownBasis[i];
            pnlUnknownBasis.Add(new PnlUnknownBasisSnapshot(u.EndClientId, u.Symbol, u.NetQuantity, u.FirmId));
        }
        var pnlSeen = new List<string>(raw.PnlSeenExecutionIds.Length);
        for (var i = 0; i < raw.PnlSeenExecutionIds.Length; i++)
            pnlSeen.Add(raw.PnlSeenExecutionIds[i]);

        var phaseOverrides = new List<SessionPhaseOverrideSnapshot>(raw.SessionPhaseOverrides.Length);
        for (var i = 0; i < raw.SessionPhaseOverrides.Length; i++)
        {
            var po = raw.SessionPhaseOverrides[i];
            phaseOverrides.Add(new SessionPhaseOverrideSnapshot(po.Symbol, po.Phase.ToString()));
        }

        var clOrdIds = new ClOrdIdRegistrySnapshot { NextPrefix = raw.ClOrdIds.NextPrefix };
        for (var i = 0; i < raw.ClOrdIds.Counters.Length; i++)
        {
            var c = raw.ClOrdIds.Counters[i];
            clOrdIds.Counters.Add(new ClOrdIdCounterSnapshot(c.EndClientId, c.PrefixIdx, c.Counter));
        }

        var algoIds = new AlgoIdRegistrySnapshot();
        for (var i = 0; i < raw.AlgoIds.Length; i++)
        {
            var c = raw.AlgoIds[i];
            algoIds.Counters.Add(new AlgoIdCounterSnapshot(c.FirmId, c.Counter));
        }

        var creds = new List<UserBotCredentialSnapshot>(raw.UserBotCredentials.Length);
        for (var i = 0; i < raw.UserBotCredentials.Length; i++)
        {
            var c = raw.UserBotCredentials[i];
            creds.Add(new UserBotCredentialSnapshot(
                c.Id, c.UserId, c.CredShortId, c.Label, c.SecretHash, c.CreatedAtUtc, c.RevokedAtUtc));
        }
        // Match the legacy InMemoryUserBotCredentialRegistry.Snapshot()
        // ordering: stable by (CreatedAtUtc, Id) so snapshot diffs stay
        // deterministic across captures.
        creds.Sort(static (a, b) =>
        {
            var c = a.CreatedAtUtc.CompareTo(b.CreatedAtUtc);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });

        var sessions = new List<BotSessionStateSnapshot>(raw.BotSessions.Length);
        for (var i = 0; i < raw.BotSessions.Length; i++)
        {
            var s = raw.BotSessions[i];
            sessions.Add(new BotSessionStateSnapshot(
                s.CredentialId, s.SessionId, s.CurrentVer, s.LastCheckpointedOutboundSeq));
        }
        sessions.Sort(static (a, b) => a.CredentialId.CompareTo(b.CredentialId));

        var botOrderMaps = new List<BotOrderMappingSnapshot>(raw.BotOrderMappings.Length);
        for (var i = 0; i < raw.BotOrderMappings.Length; i++)
        {
            var m = raw.BotOrderMappings[i];
            botOrderMaps.Add(new BotOrderMappingSnapshot(m.InternalClOrdId, m.CredentialId, m.ExternalClOrdId));
        }
        botOrderMaps.Sort(static (a, b) => a.InternalClOrdId.CompareTo(b.InternalClOrdId));

        var botCancelMaps = new List<BotCancelMappingSnapshot>(raw.BotCancelMappings.Length);
        for (var i = 0; i < raw.BotCancelMappings.Length; i++)
        {
            var m = raw.BotCancelMappings[i];
            botCancelMaps.Add(new BotCancelMappingSnapshot(
                m.CancelInternalClOrdId, m.OriginalInternalClOrdId, m.CredentialId, m.ExternalCancelClOrdId));
        }
        botCancelMaps.Sort(static (a, b) => a.CancelInternalClOrdId.CompareTo(b.CancelInternalClOrdId));

        var povProgress = new List<PovProgressSnapshot>(raw.PovProgress.Length);
        for (var i = 0; i < raw.PovProgress.Length; i++)
        {
            var p = raw.PovProgress[i];
            povProgress.Add(new PovProgressSnapshot(p.FirmId, p.AlgoId, p.MarketVolumeSeen, p.LastEvaluateAtUtc));
        }

        var peggedRepeg = new List<PeggedRepegPendingSnapshot>(raw.PeggedRepegPending.Length);
        for (var i = 0; i < raw.PeggedRepegPending.Length; i++)
        {
            var p = raw.PeggedRepegPending[i];
            peggedRepeg.Add(new PeggedRepegPendingSnapshot(
                p.FirmId, p.AlgoId, p.CancelledChildClOrdId, p.TargetPrice, p.AtUtc));
        }

        var peggedRepegHistory = new List<PeggedRepegHistorySnapshot>(raw.PeggedRepegHistory.Length);
        for (var i = 0; i < raw.PeggedRepegHistory.Length; i++)
        {
            var h = raw.PeggedRepegHistory[i];
            peggedRepegHistory.Add(new PeggedRepegHistorySnapshot(
                h.FirmId, h.AlgoId, new List<ulong>(h.ChildClOrdIds), h.EvictionLogged));
        }

        var pendingReplacements = new List<PendingReplacementSnapshot>(raw.PendingReplacements.Length);
        for (var i = 0; i < raw.PendingReplacements.Length; i++)
        {
            var r = raw.PendingReplacements[i];
            pendingReplacements.Add(new PendingReplacementSnapshot(
                OriginalClOrdId: r.OriginalClOrdId,
                NewClOrdId: r.NewClOrdId,
                OwnerEndClientId: r.OwnerEndClientId,
                Symbol: r.Symbol,
                SecurityId: r.SecurityId,
                Side: r.Side.ToString(),
                Type: r.Type.ToString(),
                NewQuantity: r.NewQuantity,
                NewPrice: r.NewPrice,
                FirmId: r.FirmId,
                ParentAlgoId: r.ParentAlgoId,
                AlgoSliceSeq: r.AlgoSliceSeq,
                RequestedTimeInForce: r.RequestedTimeInForce?.ToString(),
                RequestedStopPrice: r.RequestedStopPrice,
                RequestedGoodTillDate: r.RequestedGoodTillDate,
                CreatedAtUtc: r.CreatedAtUtc,
                AmbiguousMarginHeld: r.AmbiguousMarginHeld,
                AmbiguousAtUtc: r.AmbiguousAtUtc,
                NewRemainingNotional: r.NewRemainingNotional));
        }
        pendingReplacements.Sort(static (a, b) => a.NewClOrdId.CompareTo(b.NewClOrdId));

        return new PlatformSnapshot
        {
            Seq = raw.Seq,
            CreatedAtUtc = raw.CreatedAtUtc,
            WorkingOrders = workingOrders,
            Positions = positions,
            KilledEndClients = new List<string>(raw.KilledEndClients),
            KilledFirms = new List<string>(raw.KilledFirms),
            HaltedSymbols = new List<string>(raw.HaltedSymbols),
            DefaultSessionPhase = raw.DefaultPhase.ToString(),
            SessionPhaseOverrides = phaseOverrides,
            ClOrdIds = clOrdIds,
            Ownership = ownership,
            Algos = algos,
            AlgoIds = algoIds,
            CashBalances = cash,
            CashByEndclient = cashByEndclient,
            FeesByEndclientDay = feesByEndclientDay,
            FeeSeenExecutionIds = feeSeen,
            PnlRealizedByEndclientSymbolDay = pnlRealized,
            PnlAvgCost = pnlAvgCost,
            PnlUnknownBasis = pnlUnknownBasis,
            PnlSeenExecutionIds = pnlSeen,
            UserBotCredentials = creds,
            BotSessions = sessions,
            BotOrderMappings = botOrderMaps,
            BotCancelMappings = botCancelMaps,
            AuditedExpiredIds = raw.AuditedExpiredIds,
            PovProgress = povProgress,
            PeggedRepegPending = peggedRepeg,
            PeggedRepegHistory = peggedRepegHistory,
            PendingReplacements = pendingReplacements,
            SubAccounts = raw.SubAccounts.ToList(),
            SubAccountPositions = raw.SubAccountPositions.ToList(),
            SubAccountPnl = raw.SubAccountPnl.ToList(),
            SubAccountPnlBasis = raw.SubAccountPnlBasis.ToList(),
        };
    }

    public void Restore(PlatformSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        _orders.Restore(snap.WorkingOrders);
        _positions.Restore(snap.Positions);
        _killSwitch.Restore(snap.KilledEndClients, snap.KilledFirms);
        _symbolHalts.Restore(snap.HaltedSymbols);
        var defaultPhase = Enum.TryParse<SessionPhase>(snap.DefaultSessionPhase, ignoreCase: true, out var dp)
            ? dp : SessionPhase.Continuous;
        var overrides = snap.SessionPhaseOverrides
            .Select(o => new KeyValuePair<string, SessionPhase>(
                o.Symbol,
                Enum.TryParse<SessionPhase>(o.Phase, ignoreCase: true, out var p) ? p : SessionPhase.Continuous));
        _sessionPhases.Restore(defaultPhase, overrides);
        _clOrdIds.Restore(snap.ClOrdIds);
        _ownership.Restore(snap.Ownership);
        _algos.Restore(snap.Algos);
        _algoIds.Restore(snap.AlgoIds);
        _cash.Restore(snap.CashBalances);
        _cashKeeper?.Restore(snap.CashByEndclient);
        _feeKeeper?.Restore(snap.FeesByEndclientDay, snap.FeeSeenExecutionIds);
        _pnlKeeper?.Restore(snap.PnlRealizedByEndclientSymbolDay, snap.PnlAvgCost, snap.PnlSeenExecutionIds, snap.PnlUnknownBasis);
        // Pass-1 review (#278) P1#1. Legacy snapshots taken before
        // #271 deployed have Positions populated but PnlAvgCost empty.
        // Without this seed the next sell on a pre-existing position
        // would compute realized off a zero basis and silently
        // realise nothing. PositionSnapshot carries AverageEntryPrice
        // so we reconstruct the basis from there; the seed is a
        // no-op when PnlAvgCost is already populated (current
        // snapshot format).
        //
        // Pass-3 review (#278) P1. Also gated on PnlUnknownBasis being
        // empty: a snapshot taken AFTER pass-3 carries the unknown-
        // basis set explicitly, so we must not re-seed (re-seeding
        // would be a no-op for non-zero basis rows but would
        // re-discover the zero-basis rows and double-count the
        // skipped_zero metric).
        //
        // Pass-4 review (#278) P1#1. The PnlAvgCost.Count==0 guard
        // was wrong: a pass-2-shaped snapshot has PnlAvgCost
        // populated (the non-zero-basis rows seeded under pass-1)
        // but no PnlUnknownBasis block (the field didn't exist yet),
        // so the previous gate skipped seeding entirely and the
        // zero-basis Position rows fell back to the original
        // phantom-P&L bug. Drop the avg-cost guard and rely on
        // SeedAvgCostFromLegacyPositions being idempotent (it skips
        // keys already present in _avgCost), so re-seeding only
        // adds the zero-basis legacy rows to _unknownBasisQty.
        if (_pnlKeeper is not null
            && snap.PnlUnknownBasis.Count == 0
            && snap.Positions.Count > 0)
        {
            _pnlKeeper.SeedAvgCostFromLegacyPositions(snap.Positions);
        }
        _userBotCredentials?.Restore(snap.UserBotCredentials);
        _userBotSessions?.Restore(snap.BotSessions);
        _userBotMappings?.Restore(snap.BotOrderMappings, snap.BotCancelMappings);
        // Pass-4 review (#255). Re-mark the in-flight audit-set BEFORE
        // WAL replay starts (PersistenceRecovery calls Restore then
        // ReadFromAsync). EventReplayer.Apply(OrderExpiredEvent) for
        // events past snap.Seq also calls MarkExpiredAuditAppended;
        // both writers are HashSet.Add so the operation is idempotent.
        if (_gtdScheduler is not null && snap.AuditedExpiredIds is { Count: > 0 })
        {
            foreach (var id in snap.AuditedExpiredIds)
                _gtdScheduler.MarkExpiredAuditAppended(id);
        }
        if (_povProgress is not null)
        {
            _povProgress.Restore(snap.PovProgress.Select(p =>
                (p.FirmId, p.AlgoId, new PovProgress(p.MarketVolumeSeen, p.LastEvaluateAtUtc))));
        }
        if (_peggedRepeg is not null)
        {
            _peggedRepeg.Restore(snap.PeggedRepegPending.Select(p =>
                (p.FirmId, p.AlgoId,
                    new PeggedRepegPending(p.CancelledChildClOrdId, p.TargetPrice, p.AtUtc))));
            _peggedRepeg.RestoreHistory(snap.PeggedRepegHistory.Select(h =>
                (h.FirmId, h.AlgoId, (IReadOnlyList<ulong>)h.ChildClOrdIds, h.EvictionLogged)));
        }
        // Pass-6 review (#299) P1. Re-hydrate the in-flight cancel-
        // replace registry from the persisted snapshot and — for
        // every entry whose ambiguous-margin-held flag was set at
        // capture time — re-invoke PrepareReplaceAsync to re-establish
        // the held upsize-delta reservation the pre-crash dispatch
        // left in place. Mirrors the WAL-replay branch added in
        // pass-5 (OrderReplaceAmbiguousMarginHeldEvent) so a
        // snapshot whose Seq is past the ambiguous mark recovers
        // identically to a WAL-tail-only recovery. Also re-registers
        // the ownership replace-link so a late Replaced/Rejected ER
        // on the new ClOrdID resolves correctly through the
        // processor's replace-side branch. Restore happens BEFORE
        // WAL replay; replay's OrderReplaceRequestedEvent /
        // OrderReplaceAmbiguousMarginHeldEvent branches are
        // idempotent under TryAdd / Mark — duplicate replay (entry
        // already in the snapshot) becomes a benign no-op.
        if (_replacements is not null && snap.PendingReplacements is { Count: > 0 })
        {
            var entries = new List<PendingReplacementEntrySnapshot>(snap.PendingReplacements.Count);
            foreach (var p in snap.PendingReplacements)
            {
                var intent = new OrderReplacementIntent(
                    OriginalClOrdId: p.OriginalClOrdId,
                    NewClOrdId: p.NewClOrdId,
                    Owner: new EndClientId(p.OwnerEndClientId),
                    Symbol: p.Symbol,
                    SecurityId: p.SecurityId,
                    Side: Enum.Parse<OrderSide>(p.Side, ignoreCase: true),
                    Type: Enum.Parse<OrderType>(p.Type, ignoreCase: true),
                    NewQuantity: p.NewQuantity,
                    NewPrice: p.NewPrice,
                    FirmId: p.FirmId,
                    ParentAlgoId: p.ParentAlgoId,
                    AlgoSliceSeq: p.AlgoSliceSeq,
                    RequestedTimeInForce: p.RequestedTimeInForce is { } tif
                        ? Enum.Parse<TimeInForce>(tif, ignoreCase: true)
                        : (TimeInForce?)null,
                    RequestedStopPrice: p.RequestedStopPrice,
                    RequestedGoodTillDate: p.RequestedGoodTillDate);
                entries.Add(new PendingReplacementEntrySnapshot(
                    Intent: intent,
                    CreatedAt: p.CreatedAtUtc,
                    AmbiguousMarginHeld: p.AmbiguousMarginHeld,
                    AmbiguousAt: p.AmbiguousAtUtc,
                    NewRemainingNotional: p.NewRemainingNotional));
            }
            _replacements.Restore(entries);
            foreach (var p in snap.PendingReplacements)
            {
                // Snapshot restore runs BEFORE WAL replay, but the
                // ownership map was already restored above from
                // snap.Ownership. The orig SHOULD be present
                // (snapshot was taken while orig was working); if
                // not (terminal ER landed in the same window and the
                // orig was evicted from the ownership map by some
                // future cleanup), the replace-link registration
                // would throw — skip defensively, mirroring the
                // WAL-replay branch's `TryResolve(...)` guard.
                if (!_ownership.TryResolve(p.OriginalClOrdId, out _)) continue;
                _ownership.RegisterReplaceLink(p.OriginalClOrdId, p.NewClOrdId);
                if (p.AmbiguousMarginHeld && _replaceMargin is not null)
                {
                    // Synchronous under ReserveOnSubmitMarginProvider —
                    // same GetAwaiter().GetResult() shape as the
                    // EventReplayer's pass-5 branch.
                    _replaceMargin.PrepareReplaceAsync(
                        p.OriginalClOrdId,
                        p.NewClOrdId,
                        new EndClientId(p.OwnerEndClientId),
                        p.NewRemainingNotional,
                        CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
        }
        // Q4.1 (#301). Restore the sub-account registry + parallel
        // keepers BEFORE WAL replay starts. EventReplayer's
        // SubAccountCreated/Deactivated branches and the
        // SubAccountId-tagged RealizedPnlEvent fan-out are then
        // applied on top of the snapshot baseline — idempotent for
        // the registry (last-write-wins on Active) and additive for
        // the per-sub-account P&L bucket (delta-add). Empty lists on
        // legacy snapshots collapse to the no-sub-account world.
        _subAccounts?.Restore(snap.SubAccounts);
        _subAccountPositions?.Restore(snap.SubAccountPositions);
        _subAccountPnl?.Restore(snap.SubAccountPnl, snap.SubAccountPnlBasis);
        // PR #316 P1 (review). Backfill bucket-level avg-cost basis
        // from the (master Positions, SubAccountPositions) blocks
        // whenever a snapshot lands without a complete
        // SubAccountPnlBasis block — i.e. a pre-#316 snapshot taken
        // before the basis store existed, or a partial snapshot
        // whose basis is missing for some buckets. Without this
        // seed, the first close on a restored bucket whose basis is
        // absent would route through ApplyBucketFill's fresh-open
        // branch and emit realized PnL = 0, dropping a real economic
        // delta on the floor (the aggregate keeper would compute it
        // correctly via SeedAvgCostFromLegacyPositions above, but
        // the aggregate result is discarded once _subAccountPnl is
        // wired). Idempotent on already-present keys (a #316-shaped
        // snapshot with SubAccountPnlBasis populated is a no-op).
        if (_subAccountPnl is not null)
        {
            _subAccountPnl.SeedBucketBasisFromLegacyPositions(snap.Positions, snap.SubAccountPositions);
        }
    }
}

/// <summary>
/// Replays a single WAL event onto in-memory state. Used by recovery to
/// bring the world up-to-date past the latest snapshot. No fan-out via
/// <c>IExecutionEventSink</c> happens during replay — there are no
/// subscribers yet at startup, and re-emitting historical ERs would just
/// be noise.
/// </summary>
public sealed class EventReplayer
{
    private readonly WorkingOrderBook _orders;
    private readonly OrderOwnershipMap _ownership;
    private readonly KillSwitchService _killSwitch;
    private readonly SymbolHaltService _symbolHalts;
    private readonly SessionPhaseService _sessionPhases;
    private readonly ExecutionReportProcessor _processor;
    private readonly AlgoBook _algos;
    private readonly ClOrdIdPrefixRegistry _clOrdIds;
    private readonly AlgoIdRegistry _algoIds;
    private readonly PendingReplacementRegistry? _replacements;
    private readonly IReplaceMarginCoordinator? _replaceMargin;
    private readonly InMemoryUserBotCredentialRegistry? _userBotCredentials;
    private readonly InMemoryUserBotSessionRegistry? _userBotSessions;
    private readonly IUserBotOrderMappingRegistry? _userBotMappings;
    /// <summary>
    /// Pass-3 review (#255). Optional. When wired (production
    /// composition includes the GTD scheduler), every replayed
    /// <see cref="OrderExpiredEvent"/> calls
    /// <see cref="GtdExpirationScheduler.MarkExpiredAuditAppended"/>
    /// so the scheduler's cold-start <see cref="GtdExpirationScheduler.StartAsync"/>
    /// seeds surviving GTD orders' <c>Entry.ExpiredAuditAppended</c>
    /// to <c>true</c>, preventing a duplicate audit envelope when a
    /// crash landed between OrderExpiredEvent append and
    /// OrderCancelRequestedEvent append. Order matters:
    /// <c>RunRecoveryAndSeedingAsync</c> drains the WAL via this
    /// replayer BEFORE <c>app.Run()</c> kicks off the scheduler's
    /// hosted-service <c>StartAsync</c>.
    /// </summary>
    private readonly GtdExpirationScheduler? _gtdScheduler;
    private readonly CashKeeper? _cashKeeper;
    private readonly FeeKeeper? _feeKeeper;
    private readonly PnlKeeper? _pnlKeeper;
    private readonly SubAccountsRegistry? _subAccounts;
    private readonly SubAccountPositionKeeper? _subAccountPositions;
    private readonly SubAccountPnlKeeper? _subAccountPnl;
    private readonly IFeeCalculator? _feeCalculator;
    /// <summary>
    /// Pass-1 review (#295) P1#1. Optional. When wired, replay folds
    /// <see cref="AlgoPovSlicedEvent.MarketVolumeSeen"/> +
    /// <see cref="AlgoPovSlicedEvent.LastEvaluateAtUtc"/> into the
    /// per-POV progress book so a snapshot+tail recovery converges on
    /// the same scheduling baseline as a snapshot-only restore.
    /// </summary>
    private readonly PovProgressBook? _povProgress;
    /// <summary>
    /// Pass-1 review (#296) P1-C. Optional. When wired, replay of
    /// <see cref="AlgoPeggedRepegStartedEvent"/> sets the pending
    /// entry and replay of <see cref="AlgoPeggedRepegResolvedEvent"/>
    /// (or any <see cref="AlgoTerminalStateRecordedEvent"/>) clears
    /// it. The engine's <c>Reconcile</c> reads the resulting book to
    /// seed <c>AlgoParentRuntime.RepegPending</c> + expected-cancel
    /// marker so a post-restart cancel-ack ER routes through
    /// SubmitNextSliceAsync rather than the venue-cancel suspension
    /// path.
    /// </summary>
    private readonly PeggedRepegBook? _peggedRepeg;

    public EventReplayer(
        WorkingOrderBook orders,
        OrderOwnershipMap ownership,
        KillSwitchService killSwitch,
        SymbolHaltService symbolHalts,
        SessionPhaseService sessionPhases,
        ExecutionReportProcessor processor,
        AlgoBook algos,
        ClOrdIdPrefixRegistry clOrdIds,
        AlgoIdRegistry algoIds,
        PendingReplacementRegistry? replacements = null,
        InMemoryUserBotCredentialRegistry? userBotCredentials = null,
        InMemoryUserBotSessionRegistry? userBotSessions = null,
        IUserBotOrderMappingRegistry? userBotMappings = null,
        GtdExpirationScheduler? gtdScheduler = null,
        CashKeeper? cashKeeper = null,
        FeeKeeper? feeKeeper = null,
        IFeeCalculator? feeCalculator = null,
        PnlKeeper? pnlKeeper = null,
        PovProgressBook? povProgress = null,
        PeggedRepegBook? peggedRepeg = null,
        // Pass-5 review (#299) P1. Optional. When wired, replay of
        // <see cref="OrderReplaceAmbiguousMarginHeldEvent"/> re-calls
        // <c>PrepareReplaceAsync</c> to re-establish the held upsize-
        // delta reservation a pre-crash modify left in place after an
        // ambiguous gateway dispatch. Without this hook, capacity
        // would return on restart and a late Replaced ER would either
        // double-add (if Prepare ran again) or land in the
        // "neither side has owner" branch of <c>CommitReplace</c> —
        // both break the over-allocation invariant.
        IReplaceMarginCoordinator? replaceMargin = null,
        SubAccountsRegistry? subAccounts = null,
        SubAccountPositionKeeper? subAccountPositions = null,
        SubAccountPnlKeeper? subAccountPnl = null)
    {
        _orders = orders;
        _ownership = ownership;
        _killSwitch = killSwitch;
        _symbolHalts = symbolHalts;
        _sessionPhases = sessionPhases;
        _processor = processor;
        _algos = algos;
        _clOrdIds = clOrdIds;
        _algoIds = algoIds;
        _replacements = replacements;
        _userBotCredentials = userBotCredentials;
        _userBotSessions = userBotSessions;
        _userBotMappings = userBotMappings;
        _gtdScheduler = gtdScheduler;
        _cashKeeper = cashKeeper;
        _feeKeeper = feeKeeper;
        _feeCalculator = feeCalculator;
        _pnlKeeper = pnlKeeper;
        _povProgress = povProgress;
        _peggedRepeg = peggedRepeg;
        _replaceMargin = replaceMargin;
        _subAccounts = subAccounts;
        _subAccountPositions = subAccountPositions;
        _subAccountPnl = subAccountPnl;
    }

    /// <summary>
    /// Q2.3 (#270) pass-3 review. Called by
    /// <see cref="PersistenceRecovery"/> after the WAL drain completes
    /// to materialise any deferred fee synths (ER-fill events that
    /// were not paired with a durable
    /// <see cref="Application.Persistence.FeeAccruedEvent"/> — the true
    /// crash-window cases). No-op when fees aren't wired (legacy test
    /// configs without a calculator). Returns the number of synths
    /// materialised so the recovery driver can log a warning when this
    /// fires above zero.
    /// </summary>
    public int FinalizeReplay()
    {
        var n = 0;
        if (_feeKeeper is not null && _feeCalculator is not null)
            n += _feeKeeper.FinalizeReplay(_feeCalculator);
        if (_pnlKeeper is not null)
            // PR #316 P1.2. Route bucket-tagged synthesized deltas into
            // SubAccountPnlKeeper so sub-bucket realised totals don't
            // diverge from the live path when the durable
            // RealizedPnlEvent did not survive the ER-then-crash window.
            n += _pnlKeeper.FinalizeReplay(_subAccountPnl);
        return n;
    }

    public void Apply(WalEvent evt)
    {
        switch (evt)
        {
            case OrderSubmittedEvent o:
                var owner = new EndClientId(o.EndClientId);
                var side = Enum.Parse<OrderSide>(o.Side, ignoreCase: true);
                var type = Enum.Parse<OrderType>(o.Type, ignoreCase: true);
                // Q1.1 (#253) — older WAL segments default to "Day" via
                // the OrderSubmittedEvent record's init default, so a
                // missing field round-trips through Enum.Parse cleanly.
                var tif = Enum.Parse<TimeInForce>(o.TimeInForce, ignoreCase: true);
                // Q3.4 (#284) — older WAL segments default DisplayQty /
                // DisplayResetPolicy to null (no reserve), so a missing
                // payload round-trips as a full-disclosure order.
                DisplayResetPolicy? policy = o.DisplayResetPolicy is { } dpName
                    ? Enum.Parse<DisplayResetPolicy>(dpName, ignoreCase: true)
                    : (DisplayResetPolicy?)null;
                _orders.TryAdd(new Order(o.ClOrdId, owner, o.Symbol, o.SecurityId, side, type,
                    o.Quantity, o.Price, o.FirmId, o.ParentAlgoId, o.AlgoSliceSeq,
                    timeInForce: tif, stopPrice: o.StopPrice, goodTillDate: o.GoodTillDate,
                    displayQty: o.DisplayQty, displayResetPolicy: policy,
                    subAccountId: SubAccountId.FromNullableString(o.SubAccountId)));
                _ownership.Register(o.ClOrdId, owner);
                // #157: advance the ClOrdID registry watermark so the next
                // live Generate(owner) cannot re-allocate this ID.
                _clOrdIds.AdvanceCounterTo(owner, o.ClOrdId);
                // Sub-issue #171 (E): rebuild the bot order mapping side
                // record for FIXP-origin orders. Same call shape as the
                // live submit-time apply callback so a snapshot taken
                // mid-life and a clean restart from WAL produce identical
                // registry state.
                if (o.BotMapping is { } bm && _userBotMappings is not null)
                    _userBotMappings.RegisterOrderInternal(
                        o.ClOrdId, bm.CredentialId, bm.ExternalClOrdId);
                // Parent state-machine progression on first child accept is
                // engine-side (slice 5/6); replay only re-creates the order
                // — the parent's Working/Filled state is reconstructed from
                // the child ER stream through the processor below.
                break;
            case OrderCancelRequestedEvent ocr:
                // Sub-issue #171 (E). Re-runs the same in-memory mutations
                // the live cancel path's apply callback ran. No gateway
                // call on replay (the original Cancel was either acked
                // by the venue and its ER will replay, or it never was
                // and the operator/bot will retry).
                var ocrOwner = new EndClientId(ocr.OwnerEndClientId);
                _ownership.RegisterCancelLink(ocr.CancelClOrdId, ocr.OriginalClOrdId);
                _clOrdIds.AdvanceCounterTo(ocrOwner, ocr.CancelClOrdId);
                if (ocr.BotMapping is { } cbm && _userBotMappings is not null)
                    _userBotMappings.RegisterCancelInternal(
                        cancelInternalClOrdId: ocr.CancelClOrdId,
                        originalInternalClOrdId: ocr.OriginalClOrdId,
                        credentialId: cbm.CredentialId,
                        externalCancelClOrdId: cbm.ExternalClOrdId);
                break;
            case OrderReplaceRequestedEvent rr:
                // Slice 4 of #122. Re-register the in-flight intent and
                // the new→orig link so a subsequent Replaced/Rejected ER
                // on the new ClOrdID still resolves correctly. If the
                // orig is already gone (e.g. terminal ER replayed after
                // this event), the intent will never be consumed and the
                // entry stays as a benign artifact — same posture as a
                // re-created order whose ER stream was lost.
                if (_replacements is not null
                    && _ownership.TryResolve(rr.OriginalClOrdId, out _))
                {
                    var rrSide = Enum.Parse<OrderSide>(rr.Side, ignoreCase: true);
                    var rrType = Enum.Parse<OrderType>(rr.Type, ignoreCase: true);
                    var intent = new OrderReplacementIntent(
                        OriginalClOrdId: rr.OriginalClOrdId,
                        NewClOrdId: rr.NewClOrdId,
                        Owner: new EndClientId(rr.EndClientId),
                        Symbol: rr.Symbol,
                        SecurityId: rr.SecurityId,
                        Side: rrSide,
                        Type: rrType,
                        NewQuantity: rr.NewQuantity,
                        NewPrice: rr.NewPrice,
                        FirmId: rr.FirmId,
                        ParentAlgoId: rr.ParentAlgoId,
                        AlgoSliceSeq: rr.AlgoSliceSeq,
                        RequestedTimeInForce: rr.RequestedTimeInForce is { } rrTif
                            ? Enum.Parse<TimeInForce>(rrTif, ignoreCase: true)
                            : (TimeInForce?)null,
                        RequestedStopPrice: rr.RequestedStopPrice,
                        RequestedGoodTillDate: rr.RequestedGoodTillDate);
                    _replacements.TryAdd(intent);
                    _ownership.RegisterReplaceLink(rr.OriginalClOrdId, rr.NewClOrdId);
                }
                // #157: the new ClOrdID was generated post-snapshot and
                // must advance the watermark even if the replacement
                // intent itself wasn't re-registered (orig already gone).
                _clOrdIds.AdvanceCounterTo(new EndClientId(rr.EndClientId), rr.NewClOrdId);
                break;
            case OrderReplaceAmbiguousMarginHeldEvent amh:
                // Pass-5 review (#299) P1. Re-establish the held
                // margin reservation a pre-crash modify left in
                // place after an ambiguous gateway dispatch, and
                // re-mark the corresponding PendingReplacementRegistry
                // entry so the AlgoScheduler's TTL sweep ages from
                // the same wall-clock the pre-crash sweep would
                // have observed. Both hooks are optional — legacy
                // test compositions that omit the margin coordinator
                // or the replacement registry simply skip the side
                // they didn't wire (the other side still applies).
                if (_replaceMargin is not null)
                {
                    // PrepareReplaceAsync's production implementation
                    // (ReserveOnSubmitMarginProvider) is synchronous
                    // under a lock and returns a completed task — the
                    // GetAwaiter().GetResult() shape matches every
                    // other sync replay site here and avoids forcing
                    // the snapshotter call chain to be async.
                    _replaceMargin.PrepareReplaceAsync(
                        amh.OriginalClOrdId,
                        amh.NewClOrdId,
                        new EndClientId(amh.EndClientId),
                        amh.NewRemainingNotional,
                        CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                _replacements?.MarkAmbiguousMarginHeld(amh.NewClOrdId, amh.HeldAtUtc, amh.NewRemainingNotional);
                break;
            case ExecutionReportReceivedEvent er:
                if (Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind))
                {
                    _processor.Apply(er.ClOrdId, kind, er.LeavesQuantity, er.CumulativeQuantity,
                        er.LastQuantity, er.LastPrice, er.RejectReason, er.OrigClOrdId, isReplay: true, eventTimestampUtc: er.TimestampUtc);
                }
                // #157: cancel-side ClOrdIDs are generated by the
                // submission/modify/cancel paths but not represented by
                // OrderSubmittedEvent or OrderReplaceRequestedEvent. The
                // ER is the only durable record carrying them. Resolve
                // owner via the (now-replayed) ownership map; OrigClOrdId
                // covers the cancel-replace ack case where ClOrdId is the
                // brand-new ID and OrigClOrdId is the pre-existing order
                // we know the owner of.
                EndClientId? erOwner = null;
                if (_ownership.TryResolve(er.ClOrdId, out var directOwner) && directOwner is not null)
                    erOwner = directOwner;
                else if (er.OrigClOrdId is { } origId && _ownership.TryResolve(origId, out var origOwner) && origOwner is not null)
                    erOwner = origOwner;
                if (erOwner is { } resolvedOwner)
                    _clOrdIds.AdvanceCounterTo(resolvedOwner, er.ClOrdId);
                break;
            case KillSwitchToggledEvent k:
                if (k.Scope.Equals("end-client", StringComparison.OrdinalIgnoreCase))
                {
                    if (k.Killed) _killSwitch.KillEndClient(new EndClientId(k.Target));
                    else _killSwitch.ReviveEndClient(new EndClientId(k.Target));
                }
                else if (k.Scope.Equals("firm", StringComparison.OrdinalIgnoreCase))
                {
                    if (k.Killed) _killSwitch.KillFirm(k.Target);
                    else _killSwitch.ReviveFirm(k.Target);
                }
                break;
            case SymbolHaltToggledEvent sh:
                if (sh.Halted) _symbolHalts.Halt(sh.Symbol);
                else _symbolHalts.Resume(sh.Symbol);
                break;
            case SessionPhaseChangedEvent sp:
                if (string.IsNullOrWhiteSpace(sp.Symbol))
                {
                    if (Enum.TryParse<SessionPhase>(sp.Phase, ignoreCase: true, out var defPhase))
                        _sessionPhases.SetDefaultPhase(defPhase);
                }
                else if (sp.Cleared)
                {
                    _sessionPhases.ClearPhase(sp.Symbol);
                }
                else if (Enum.TryParse<SessionPhase>(sp.Phase, ignoreCase: true, out var symPhase))
                {
                    _sessionPhases.SetPhase(sp.Symbol, symPhase);
                }
                break;
            case AlgoCreatedEvent ac:
                ApplyAlgoCreated(ac);
                break;
            case AlgoCancelRequestedEvent acr:
                if (_algos.TryGet(acr.FirmId, acr.AlgoId, out var cancelling) && cancelling is not null)
                    cancelling.RequestCancel();
                break;
            case AlgoTerminalStateRecordedEvent at:
                if (_algos.TryGet(at.FirmId, at.AlgoId, out var algo) && algo is not null)
                {
                    var status = Enum.Parse<AlgoStatus>(at.Status, ignoreCase: true);
                    var reason = Enum.Parse<AlgoTerminalReason>(at.Reason, ignoreCase: true);
                    algo.RecordTerminal(status, reason, at.AtUtc);
                }
                // Pass-1 review (#296) P1-C. Drop any in-flight repeg
                // marker on parent terminal so the book stays bounded
                // and a future snapshot doesn't carry stale state.
                //
                // Pass-5 review (#296) P1. Also drop the cancelled-
                // child history ring — once the parent is terminal no
                // further late ERs can affect routing, so the dedup
                // memory is dead weight.
                _peggedRepeg?.RemoveAll(at.FirmId, at.AlgoId);
                break;
            case AlgoPovSlicedEvent ps:
                // Pass-1 review (#295) P1#1. Restore the POV scheduling
                // baseline so the post-restart engine slices off the
                // PRE-restart cumulative-market-volume baseline, not zero.
                // Last-write-wins: events are replayed in seq order so
                // the final state matches the most recently persisted
                // slice. Idempotent under double-replay.
                _povProgress?.Set(ps.FirmId, ps.AlgoId, ps.MarketVolumeSeen, ps.LastEvaluateAtUtc);
                break;
            case AlgoPeggedRepegStartedEvent pgs:
                // Pass-1 review (#296) P1-C. Record the pending repeg
                // cycle so the engine's Reconcile pass can rebuild
                // AlgoParentRuntime.RepegPending + the expected-cancel
                // marker post-restart. Last-write-wins under replay.
                _peggedRepeg?.Set(pgs.FirmId, pgs.AlgoId,
                    pgs.CancelledChildClOrdId, pgs.TargetPrice, pgs.AtUtc);
                // Pass-5 review (#296) P1. Add the cancelled child id
                // to the per-parent dedup history ring so a late
                // terminal ER for THIS cancel survives across:
                //   * subsequent repeg cycles (the single-slot
                //     LastRepegCancelledChildId would have moved on);
                //   * a snapshot+replay round-trip (the ring is
                //     additively snapshotted alongside the pending
                //     entry, so even post-snapshot WAL replay rebuilds
                //     historic cycles deterministically).
                // Cap-bounded FIFO so memory stays O(cycles_in_window).
                _peggedRepeg?.MarkCancelledChild(pgs.FirmId, pgs.AlgoId, pgs.CancelledChildClOrdId);
                break;
            case AlgoPeggedRepegResolvedEvent pgr:
                // Pass-1 review (#296) P1-C. Cancel-ack was consumed
                // and the replacement was submitted; clear the
                // pending marker so a snapshot+tail recovery converges
                // on the same in-memory state as a snapshot-only
                // restore.
                _peggedRepeg?.Remove(pgr.FirmId, pgr.AlgoId);
                break;
            case OrderStaledEvent os:
                if (_orders.TryGet(os.ClOrdId, out var staleOrd) && staleOrd is not null)
                    staleOrd.MarkStale(os.Reason, os.StaledAtUtc);
                break;
            case OrderStaleClearedEvent osc:
                if (_orders.TryGet(osc.ClOrdId, out var clearOrd) && clearOrd is not null)
                    clearOrd.ClearStale();
                break;
            case UserBotCredentialCreatedEvent ubc:
                _userBotCredentials?.ApplyCreated(new UserBotCredential(
                    ubc.Id, ubc.UserId, ubc.CredShortId, ubc.Label, ubc.SecretHash,
                    ubc.CreatedAtUtc, RevokedAtUtc: null));
                break;
            case UserBotCredentialRevokedEvent ubr:
                _userBotCredentials?.ApplyRevoked(ubr.Id, ubr.RevokedAtUtc);
                break;
            case BotSessionInitializedEvent bsi:
                _userBotSessions?.ApplyInitialized(new BotSessionState(
                    bsi.CredentialId, bsi.SessionId, bsi.InitialVer, LastCheckpointedOutboundSeq: 0));
                break;
            case BotSessionVerAdvancedEvent bsv:
                _userBotSessions?.ApplyVerAdvanced(bsv.CredentialId, bsv.NewVer);
                break;
            case BotSessionSeqAdvancedEvent bss:
                _userBotSessions?.ApplyCheckpointedSeq(bss.CredentialId, bss.CheckpointedOutboundSeq);
                break;
            case OrderExpiredEvent oe:
                // Pass-3 review (#255). The audit envelope itself is a
                // no-op for in-memory state — the downstream Canceled ER
                // (also on the WAL) drives the order's terminal
                // transition. But we MUST inform the GTD scheduler that
                // this audit is durably on disk so its cold-start
                // Schedule() does not re-emit a duplicate when the
                // pre-crash cancel ER never landed. Null-tolerant for
                // compositions / tests that don't wire a scheduler.
                _gtdScheduler?.MarkExpiredAuditAppended(oe.ClOrdId);
                break;
            case CashLedgerEvent cle:
                // Q2.2 (#269). Replay folds the deposit/withdrawal into
                // CashKeeper. Null-tolerant for compositions/tests that
                // don't wire the keeper.
                _cashKeeper?.Apply(cle.Operation, new EndClientId(cle.EndClientId), cle.Amount);
                break;
            case FeeAccruedEvent fae:
                // Q2.3 (#270). Forward the accrual to FeeKeeper. The
                // keeper itself dedupes on ExecutionId so a snapshot
                // whose totals already include this event is left
                // untouched (FeeSeenExecutionIds restored alongside the
                // totals — see StateSnapshotter.Restore).
                _feeKeeper?.Apply(fae);
                break;
            case RealizedPnlEvent rpe:
                // Q2.4 (#271). Forward to PnlKeeper. Apply uses
                // RunningTotal as authoritative so a snapshot+tail
                // recovery converges on the persisted value even if the
                // basis tracker projection drifts.
                _pnlKeeper?.Apply(rpe);
                // Q4.1 (#301). Sub-account-tagged realized rows also
                // flow into the per-sub-account keeper. Legacy events
                // pre-dating the field carry null and short-circuit.
                // The fan-out is firm-scoped: FirmId is emitted
                // alongside SubAccountId at append time (PR review
                // #301 P1). For belt-and-braces parity with WAL
                // segments written between the original #301 merge
                // and this fix — where SubAccountId may be set but
                // FirmId is missing — fall back to "default", which
                // is the same sentinel the API uses when no firm
                // claim is present.
                if (rpe.SubAccountId is { } subPnlId)
                    _subAccountPnl?.Add(
                        rpe.FirmId ?? "default",
                        rpe.EndClientId, new SubAccountId(subPnlId),
                        rpe.Symbol, rpe.DayKey, rpe.DeltaRealized);
                break;
            case SubAccountCreatedEvent sac:
                // Q4.1 (#301). Seeds / revives the registry row. The
                // submit pipeline gate reads IsActive directly off this
                // map; deactivated entries that get re-created here
                // flip back to active.
                _subAccounts?.ApplyCreated(sac.FirmId, sac.Id, sac.DisplayName);
                break;
            case SubAccountDeactivatedEvent sad:
                // Q4.1 (#301). Soft-delete — the entry stays in the map
                // (so historical orders still resolve) but IsActive
                // flips to false and the submit gate rejects new ones.
                _subAccounts?.ApplyDeactivated(sad.FirmId, sad.Id);
                break;
        }
    }

    private void ApplyAlgoCreated(AlgoCreatedEvent ac)
    {
        _algoIds.AdvanceCounterTo(ac.FirmId, ac.AlgoId);
        var owner = new EndClientId(ac.EndClientId);
        var side = Enum.Parse<OrderSide>(ac.Side, ignoreCase: true);
        var type = Enum.Parse<AlgoType>(ac.Type, ignoreCase: true);
        AlgoParameters parameters = type switch
        {
            AlgoType.Iceberg => new IcebergParameters(
                ac.IcebergDisplayQuantity ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing IcebergDisplayQuantity."),
                ac.IcebergLimitPrice),
            AlgoType.Twap => new TwapParameters(
                ac.TwapStartUtc ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing TwapStartUtc."),
                ac.TwapEndUtc ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing TwapEndUtc."),
                ac.TwapSliceCount ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing TwapSliceCount."),
                Enum.Parse<OrderType>(ac.TwapChildOrderType ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing TwapChildOrderType."), ignoreCase: true),
                ac.TwapChildPrice),
            AlgoType.Vwap => new VwapParameters(
                ac.VwapStartUtc ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing VwapStartUtc."),
                ac.VwapEndUtc ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing VwapEndUtc."),
                Enum.Parse<OrderType>(ac.VwapChildOrderType ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing VwapChildOrderType."), ignoreCase: true),
                ac.VwapChildPrice,
                TimeSpan.FromTicks(ac.VwapTickIntervalTicks ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing VwapTickIntervalTicks.")),
                ac.VwapSliceMaxPct,
                ac.VwapPriceLimit,
                ac.VwapParticipationCap),
            AlgoType.Pov => new PovParameters(
                ac.PovStartUtc ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing PovStartUtc."),
                ac.PovEndUtc ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing PovEndUtc."),
                Enum.Parse<OrderType>(ac.PovChildOrderType ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing PovChildOrderType."), ignoreCase: true),
                ac.PovChildPrice,
                ac.PovParticipationRate ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing PovParticipationRate."),
                TimeSpan.FromTicks(ac.PovTickIntervalTicks ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing PovTickIntervalTicks.")),
                ac.PovPriceLimit,
                ac.PovMinSliceQty ?? 1L),
            AlgoType.Pegged => new PeggedParameters(
                Enum.Parse<PegRef>(ac.PeggedRef ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing PeggedRef."), ignoreCase: true),
                ac.PeggedOffsetTicks ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing PeggedOffsetTicks."),
                TimeSpan.FromTicks(ac.PeggedRepegIntervalTicks ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing PeggedRepegIntervalTicks.")),
                ac.PeggedTickSize ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing PeggedTickSize."),
                Enum.Parse<OrderType>(ac.PeggedChildOrderType ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing PeggedChildOrderType."), ignoreCase: true),
                ac.PeggedPriceLimit),
            _ => throw new InvalidOperationException($"Unknown algo type: {ac.Type}"),
        };
        _algos.TryAdd(new Algo(ac.AlgoId, owner, ac.FirmId, ac.Symbol, ac.SecurityId,
            side, type, ac.TotalQuantity, parameters, ac.CreatedAtUtc));
    }
}
