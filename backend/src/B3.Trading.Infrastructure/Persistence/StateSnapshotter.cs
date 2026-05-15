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
        FeeKeeper? feeKeeper = null)
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
        _userBotCredentials = userBotCredentials;
        _userBotSessions = userBotSessions;
        _userBotMappings = userBotMappings;
        _gtdScheduler = gtdScheduler;
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
        UserBotCredentials = _userBotCredentials?.RawSnapshot() ?? Array.Empty<UserBotCredential>(),
        BotSessions = _userBotSessions?.RawSnapshot() ?? Array.Empty<BotSessionState>(),
        BotOrderMappings = _userBotMappings?.RawSnapshotOrders() ?? Array.Empty<BotOrderMappingRaw>(),
        BotCancelMappings = _userBotMappings?.RawSnapshotCancels() ?? Array.Empty<BotCancelMappingRaw>(),
        AuditedExpiredIds = _gtdScheduler?.SnapshotAuditedExpiredIds() ?? Array.Empty<ulong>(),
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
            });
        }

        var algos = new List<AlgoSnapshot>(raw.Algos.Length);
        for (var i = 0; i < raw.Algos.Length; i++)
            algos.Add(AlgoBook.ProjectRaw(raw.Algos[i]));

        var positions = new List<PositionSnapshot>(raw.Positions.Length);
        for (var i = 0; i < raw.Positions.Length; i++)
        {
            var p = raw.Positions[i];
            positions.Add(new PositionSnapshot(p.EndClientId, p.Symbol, p.NetQuantity, p.AverageEntryPrice));
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
            UserBotCredentials = creds,
            BotSessions = sessions,
            BotOrderMappings = botOrderMaps,
            BotCancelMappings = botCancelMaps,
            AuditedExpiredIds = raw.AuditedExpiredIds,
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
        FeeKeeper? feeKeeper = null)
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
                _orders.TryAdd(new Order(o.ClOrdId, owner, o.Symbol, o.SecurityId, side, type,
                    o.Quantity, o.Price, o.FirmId, o.ParentAlgoId, o.AlgoSliceSeq,
                    timeInForce: tif, stopPrice: o.StopPrice, goodTillDate: o.GoodTillDate));
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
            case ExecutionReportReceivedEvent er:
                if (Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind))
                {
                    _processor.Apply(er.ClOrdId, kind, er.LeavesQuantity, er.CumulativeQuantity,
                        er.LastQuantity, er.LastPrice, er.RejectReason, er.OrigClOrdId);
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
            _ => throw new InvalidOperationException($"Unknown algo type: {ac.Type}"),
        };
        _algos.TryAdd(new Algo(ac.AlgoId, owner, ac.FirmId, ac.Symbol, ac.SecurityId,
            side, type, ac.TotalQuantity, parameters, ac.CreatedAtUtc));
    }
}
