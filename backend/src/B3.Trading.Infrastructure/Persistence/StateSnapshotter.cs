using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
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
    private readonly InMemoryUserBotCredentialRegistry? _userBotCredentials;
    private readonly InMemoryUserBotSessionRegistry? _userBotSessions;
    private readonly IUserBotOrderMappingRegistry? _userBotMappings;

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
        IUserBotOrderMappingRegistry? userBotMappings = null)
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
        _userBotCredentials = userBotCredentials;
        _userBotSessions = userBotSessions;
        _userBotMappings = userBotMappings;
    }

    public PlatformSnapshot Capture(long seq) => new()
    {
        Seq = seq,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        WorkingOrders = _orders.Snapshot().ToList(),
        Positions = _positions.Snapshot().ToList(),
        KilledEndClients = _killSwitch.ListKilledEndClients().ToList(),
        KilledFirms = _killSwitch.ListKilledFirms().ToList(),
        HaltedSymbols = _symbolHalts.ListHalted().ToList(),
        DefaultSessionPhase = _sessionPhases.DefaultPhase.ToString(),
        SessionPhaseOverrides = _sessionPhases.ListOverrides()
            .Select(kv => new SessionPhaseOverrideSnapshot(kv.Key, kv.Value.ToString()))
            .ToList(),
        ClOrdIds = _clOrdIds.Snapshot(),
        Ownership = _ownership.Snapshot().ToList(),
        Algos = _algos.Snapshot().ToList(),
        AlgoIds = _algoIds.Snapshot(),
        CashBalances = _cash.Snapshot().ToList(),
        UserBotCredentials = _userBotCredentials?.Snapshot().ToList() ?? new(),
        BotSessions = _userBotSessions?.Snapshot().ToList() ?? new(),
        BotOrderMappings = _userBotMappings?.SnapshotOrders().ToList() ?? new(),
        BotCancelMappings = _userBotMappings?.SnapshotCancels().ToList() ?? new(),
    };

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
        _userBotCredentials?.Restore(snap.UserBotCredentials);
        _userBotSessions?.Restore(snap.BotSessions);
        _userBotMappings?.Restore(snap.BotOrderMappings, snap.BotCancelMappings);
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
        IUserBotOrderMappingRegistry? userBotMappings = null)
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
    }

    public void Apply(WalEvent evt)
    {
        switch (evt)
        {
            case OrderSubmittedEvent o:
                var owner = new EndClientId(o.EndClientId);
                var side = Enum.Parse<OrderSide>(o.Side, ignoreCase: true);
                var type = Enum.Parse<OrderType>(o.Type, ignoreCase: true);
                _orders.TryAdd(new Order(o.ClOrdId, owner, o.Symbol, o.SecurityId, side, type,
                    o.Quantity, o.Price, o.FirmId, o.ParentAlgoId, o.AlgoSliceSeq));
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
                        AlgoSliceSeq: rr.AlgoSliceSeq);
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
