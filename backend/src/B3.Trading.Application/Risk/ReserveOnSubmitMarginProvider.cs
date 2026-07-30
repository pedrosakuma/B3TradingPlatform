using System.Collections.Concurrent;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk;

/// <summary>
/// In-process reserve-on-submit margin provider.
///
/// <para>
/// Implements the v2 margin model documented in
/// <c>docs/rfcs/pre-trade-risk-v2.md</c> §3.1 — a synchronous
/// reservation ledger inspired by crypto spot exchanges:
/// </para>
/// <list type="bullet">
///   <item>On submit: <c>available -= price · qty</c> for the owner.</item>
///   <item>On partial fill: release <c>fillQty · price</c>.</item>
///   <item>On terminal status (Filled/Canceled/Rejected): release the
///     remaining reservation.</item>
/// </list>
///
/// <para>
/// Only Buy + Limit + non-null price orders consume cash. Sell orders
/// release inventory (no cash margin in the spot model) and Market
/// orders are sized at the venue, so we cannot reserve up-front
/// without a live reference price — both paths short-circuit to
/// <see cref="RiskDecision.Approve"/> here. Out-of-scope work in the
/// RFC: a Market-order path that uses a live reference price (slice
/// 5) and short-sale collateral (future RFC).
/// </para>
///
/// <para>
/// State is in-process and not concurrency-safe across processes — same
/// posture as <see cref="PositionKeeper"/>. Persistence recovery rebuilds
/// reservations from surviving working orders after replay.
/// </para>
///
/// <para>
/// <b>Cash source (slice 2 of #107):</b> when a <see cref="CashLedger"/>
/// is wired in, the per-(firm, owner) base capacity is read from the ledger's
/// settled-cash balance — this is the post-fill number, so a Buy that
/// just executed correctly reduces the available figure for the next
/// reservation. When the ledger has no entry for the owner, the
/// provider falls back to <c>RiskOptions.Margin.Initial</c> so legacy
/// dogfood configs keep working until slice 4 retires the option.
/// </para>
/// </summary>
public sealed class ReserveOnSubmitMarginProvider : IMarginProvider, IReplaceMarginCoordinator
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly ILogger<ReserveOnSubmitMarginProvider> _logger;
    private readonly CashLedger? _cash;
    private readonly IMarketValueCalculator _values;

    private readonly ConcurrentDictionary<ulong, ReservationEntry> _reservations = new();
    private readonly ConcurrentDictionary<MarginAccountKey, decimal> _reserved =
        new(MarginAccountKeyComparer.Instance);
    private readonly object _gate = new();

    public ReserveOnSubmitMarginProvider(
        IOptionsMonitor<RiskOptions> options,
        ILogger<ReserveOnSubmitMarginProvider> logger,
        CashLedger? cash = null,
        IMarketValueCalculator? values = null)
    {
        _options = options;
        _logger = logger;
        _cash = cash;
        _values = values ?? EquityMarketValueCalculator.Instance;
    }

    public Task<RiskDecision> TryReserveAsync(ulong clOrdId, RiskContext ctx, CancellationToken ct)
    {
        // Spot model: only Buys consume cash. Sells release inventory
        // (handled separately by the position keeper) and don't touch
        // the cash ledger here.
        if (ctx.Side != OrderSide.Buy)
            return Task.FromResult(RiskDecision.Approve);

        // Market orders are sized at the venue — without a live
        // reference price we can't reserve up-front. Slice 5 of the
        // RFC will revisit this once MarketDataReferencePrice lands.
        if (!ctx.Price.HasValue)
            return Task.FromResult(RiskDecision.Approve);

        // Pass-3 alignment (#253): the cancel-replace pipeline gates
        // its re-baseline on OrderType.IsMarginBearing(); the submit
        // path must use the IDENTICAL predicate. Otherwise a buy
        // Market or buy StopLoss carrying a stray Price would reserve
        // here at submit but commit 0 on replace, silently freeing the
        // hold while the order is still working.
        if (!ctx.Type.IsMarginBearing())
            return Task.FromResult(RiskDecision.Approve);

        var notional = _values.GetNotional(ctx.Symbol, ctx.Price.Value, ctx.Quantity);
        if (notional <= 0m)
            return Task.FromResult(RiskDecision.Approve);

        var owner = ctx.Owner.Value;
        var account = MarginAccountKey.Create(ctx.FirmId, owner);
        var baseAvailable = ResolveBaseAvailable(account);

        // Atomic check+reserve: take the gate, snapshot reserved,
        // verify capacity, mutate. The provider is a singleton so the
        // gate scope covers all races for the same owner.
        lock (_gate)
        {
            var reserved = _reserved.GetValueOrDefault(account, 0m);
            var available = baseAvailable - reserved;
            if (notional > available)
            {
                return Task.FromResult(RiskDecision.Reject(
                    $"insufficient margin: notional {notional} exceeds available {available} for firm/end-client '{ctx.FirmId}/{owner}'"));
            }
            _reserved[account] = reserved + notional;
            _reservations[clOrdId] = new ReservationEntry(
                ctx.FirmId, owner, ctx.Price.Value, ctx.Quantity, notional);
        }

        return Task.FromResult(RiskDecision.Approve);
    }

    public void OnExecution(ulong clOrdId, ExecKind kind, long lastQty)
    {
        // #153. Every state lookup happens INSIDE _gate so a
        // Suspended/Restored race with a genuine ER for the same
        // ClOrdID cannot read a stale snapshot and double-decrement
        // (the rubber-duck flagged that scenario where DecrementReserved's
        // clamp-to-zero would silently consume another order's hold).
        lock (_gate)
        {
            if (!_reservations.TryGetValue(clOrdId, out var entry))
                return; // unknown order (Sell, Market, never reserved here, or already terminalized)

            switch (kind)
            {
                case ExecKind.PartialFill:
                    if (lastQty > 0) ReleasePartial_Locked(clOrdId, entry, lastQty);
                    break;

                case ExecKind.Fill:
                    if (lastQty > 0)
                    {
                        ReleasePartial_Locked(clOrdId, entry, lastQty);
                        // Re-fetch — ReleasePartial_Locked rewrote the entry.
                        if (!_reservations.TryGetValue(clOrdId, out entry)) break;
                    }
                    ReleaseRemaining_Locked(clOrdId, entry);
                    break;

                case ExecKind.Canceled:
                case ExecKind.Rejected:
                    ReleaseRemaining_Locked(clOrdId, entry);
                    break;

                case ExecKind.Suspended:
                    // #153. Stale flip: release the cash hold so the
                    // ghost stops blocking new trading. Idempotent: a
                    // second Suspended on an already-suspended entry
                    // is a no-op (the flag prevents double-decrement).
                    if (!entry.IsSuspended && entry.RemainingNotional > 0m)
                    {
                        DecrementReserved(entry.Account, entry.RemainingNotional);
                    }
                    _reservations[clOrdId] = entry with { IsSuspended = true };
                    break;

                case ExecKind.Restored:
                    // #153. Admin clear-stale: re-acquire the hold.
                    // Restore never fails — the WAL event is already
                    // committed and refusing to track the cash would
                    // leave the ledger inconsistent with the WAL. If
                    // the increment exceeds the owner's base
                    // capacity, log + emit the overcommit metric so
                    // operators can reconcile by cancelling other
                    // stale orders.
                    if (entry.IsSuspended && entry.RemainingNotional > 0m)
                    {
                        var current = _reserved.GetValueOrDefault(entry.Account, 0m);
                        var next = current + entry.RemainingNotional;
                        var baseCap = ResolveBaseAvailable(entry.Account);
                        if (next > baseCap)
                        {
                            _logger.LogWarning(
                                "Margin restore for {ClOrdId} overcommits {Firm}/{Owner}: reserved {Current} + restored {Restored} > base {Base}.",
                                clOrdId, entry.FirmId, entry.Owner, current, entry.RemainingNotional, baseCap);
                            MetricsRegistry.MarginOvercommitOnRestore.Add(
                                1, new KeyValuePair<string, object?>("owner", entry.Owner));
                        }
                        _reserved[entry.Account] = next;
                    }
                    _reservations[clOrdId] = entry with { IsSuspended = false };
                    break;

                    // New / Replaced: nothing to release. Replaced is handled
                    // by the gateway re-issuing under a fresh ClOrdID; the
                    // original reservation stays with the original ID.
            }
        }
    }

    public void ReleaseReservation(ulong clOrdId)
    {
        lock (_gate)
        {
            if (_reservations.TryGetValue(clOrdId, out var entry))
                ReleaseRemaining_Locked(clOrdId, entry);
        }
    }

    /// <summary>
    /// Admin account-reset support (#671 / RFC #753). Removes every
    /// per-ClOrdID reservation entry tracked for (<paramref name="firmId"/>,
    /// <paramref name="owner"/>) — including suspended ones, which hold no
    /// cash in <c>_reserved</c> but must still stop tracking so a later
    /// Restored ER can't re-acquire a hold against a reset account — and
    /// clears the account's aggregate reserved notional. Runs under
    /// <see cref="_gate"/> so it can't race a concurrent
    /// <see cref="TryReserveAsync"/>/<see cref="OnExecution"/> for the same
    /// account. Idempotent: an account with nothing reserved is a no-op.
    /// </summary>
    public void ReleaseAllReservationsForAccount(string firmId, EndClientId owner)
    {
        var account = MarginAccountKey.Create(firmId, owner.Value);
        lock (_gate)
        {
            foreach (var clOrdId in _reservations
                         .Where(kv => MarginAccountKeyComparer.Instance.Equals(kv.Value.Account, account))
                         .Select(kv => kv.Key)
                         .ToArray())
            {
                _reservations.TryRemove(clOrdId, out _);
            }
            _reserved.TryRemove(account, out _);
        }
    }

    /// <summary>
    /// Rebuilds the complete reservation ledger after snapshot restore,
    /// WAL replay, and session-version reconciliation. Any temporary state
    /// produced while replaying replace events is discarded in favour of
    /// the final order book and pending-replacement registry.
    /// </summary>
    public (int Orders, int Replacements) RestoreRecoveryState(
        IEnumerable<Persistence.OrderSnapshot> orders,
        IEnumerable<PendingReplacementEntrySnapshot>? pendingReplacements = null)
    {
        ArgumentNullException.ThrowIfNull(orders);

        var restoredOrders = 0;
        var restoredReplacements = 0;
        lock (_gate)
        {
            _reservations.Clear();
            _reserved.Clear();
            if (!_options.CurrentValue.Margin.Enabled)
                return (0, 0);

            foreach (var order in orders)
            {
                if (!Enum.TryParse<OrderStatus>(order.Status, ignoreCase: true, out var status)
                    || status is not (OrderStatus.PendingNew or OrderStatus.Working or OrderStatus.PartiallyFilled)
                    || !Enum.TryParse<OrderSide>(order.Side, ignoreCase: true, out var side)
                    || side != OrderSide.Buy
                    || !Enum.TryParse<OrderType>(order.Type, ignoreCase: true, out var type)
                    || !type.IsMarginBearing()
                    || order.Price is not { } price
                    || order.LeavesQuantity <= 0)
                {
                    continue;
                }

                var remainingNotional = _values.GetNotional(order.Symbol, price, order.LeavesQuantity);
                if (remainingNotional <= 0m)
                    continue;

                var owner = order.EndClientId;
                _reservations[order.ClOrdId] = new ReservationEntry(
                    order.FirmId,
                    owner,
                    price,
                    order.LeavesQuantity,
                    remainingNotional,
                    IsSuspended: order.IsStale);

                if (!order.IsStale)
                {
                    AddRecoveredReservation_Locked(
                        order.FirmId,
                        owner,
                        remainingNotional,
                        order.ClOrdId,
                        "working_order");
                }

                restoredOrders++;
            }

            if (pendingReplacements is not null)
            {
                foreach (var pending in pendingReplacements)
                {
                    if (!pending.AmbiguousMarginHeld
                        || _reservations.ContainsKey(pending.Intent.NewClOrdId))
                    {
                        continue;
                    }

                    var owner = pending.Intent.Owner.Value;
                    var originalHeld = _reservations.TryGetValue(
                            pending.Intent.OriginalClOrdId, out var original)
                        && !original.IsSuspended
                            ? original.RemainingNotional
                            : 0m;
                    var transientDelta = Math.Max(
                        0m, pending.NewRemainingNotional - originalHeld);

                    _reservations[pending.Intent.NewClOrdId] = new ReservationEntry(
                        pending.Intent.FirmId, owner, 0m, 0L, transientDelta);
                    if (transientDelta > 0m)
                    {
                        AddRecoveredReservation_Locked(
                            pending.Intent.FirmId,
                            owner,
                            transientDelta,
                            pending.Intent.NewClOrdId,
                            "pending_replace");
                    }
                    restoredReplacements++;
                }
            }
        }
        return (restoredOrders, restoredReplacements);
    }

    private void AddRecoveredReservation_Locked(
        string firmId,
        string owner,
        decimal amount,
        ulong clOrdId,
        string source)
    {
        var account = MarginAccountKey.Create(firmId, owner);
        var next = _reserved.GetValueOrDefault(account, 0m) + amount;
        var baseCap = ResolveBaseAvailable(account);
        if (next > baseCap)
        {
            _logger.LogWarning(
                "Margin recovery for {ClOrdId} overcommits {Firm}/{Owner}: source={Source} restored reserved {Reserved} > base {Base}.",
                clOrdId, firmId, owner, source, next, baseCap);
            MetricsRegistry.MarginOvercommitOnRestore.Add(
                1, new KeyValuePair<string, object?>("owner", owner));
        }
        _reserved[account] = next;
    }

    private void ReleasePartial_Locked(ulong clOrdId, ReservationEntry entry, long lastQty)
    {
        // OPT-B (#484). Release per-contract using the originally
        // reserved per-unit notional (Price * multiplier for options,
        // Price for equity). Recomputing from entry.Price * lastQty
        // would under-release options by exactly the contract
        // multiplier and leak reserved cash for the lifetime of the
        // order.
        var perUnit = entry.OriginalQty > 0
            ? entry.OriginalNotional / entry.OriginalQty
            : entry.Price;
        var amount = perUnit * lastQty;
        if (amount <= 0m) return;
        var newRemaining = entry.RemainingNotional - amount;
        if (newRemaining < 0m)
        {
            // Should not happen if the exchange respects our qty,
            // but defend against a misbehaving venue rather than
            // letting reserved go negative.
            _logger.LogWarning(
                "Margin partial-release for {ClOrdId} would exceed remaining ({Amount} > {Remaining}); clamping.",
                clOrdId, amount, entry.RemainingNotional);
            amount = entry.RemainingNotional;
            newRemaining = 0m;
        }
        // #153. Suspended entries already had their cash released by
        // ExecKind.Suspended; partial fills must reduce the tracked
        // remaining notional (so a later Restored re-acquires only the
        // post-fill leaves) WITHOUT decrementing _reserved again.
        if (!entry.IsSuspended)
        {
            DecrementReserved(entry.Account, amount);
        }
        _reservations[clOrdId] = entry with { RemainingNotional = newRemaining };
    }

    private void ReleaseRemaining_Locked(ulong clOrdId, ReservationEntry entry)
    {
        if (!_reservations.TryRemove(clOrdId, out _)) return;
        if (entry.RemainingNotional <= 0m) return;
        // #153. Suspended entries' cash was already released; only
        // remove the tracking entry, do not double-decrement.
        if (!entry.IsSuspended)
        {
            DecrementReserved(entry.Account, entry.RemainingNotional);
        }
    }

    private void DecrementReserved(MarginAccountKey account, decimal amount)
    {
        var reserved = _reserved.GetValueOrDefault(account, 0m);
        var next = reserved - amount;
        if (next < 0m) next = 0m;
        _reserved[account] = next;
    }

    /// <summary>
    /// Resolves the per-(firm, owner) base capacity that the reservation ledger
    /// debits against. Slice 2 of #107 introduces a CashLedger fallback:
    /// when the ledger has an entry for the owner (seeded or built up
    /// from fills) it is the authoritative settled-cash figure; the
    /// owner-pinned <c>RiskOptions.Margin.Initial</c> is consulted as a
    /// fallback so existing dogfood configs keep working until slice 4
    /// retires the option.
    ///
    /// <para>
    /// The ledger answer is preferred even when it's lower than the
    /// config — a trader who's already debited cash via Buy fills must
    /// see the post-settlement number, not the original allowance.
    /// </para>
    /// </summary>
    private decimal ResolveBaseAvailable(MarginAccountKey account)
    {
        if (_cash is not null
            && _cash.TryGet(account.FirmId, new EndClientId(account.Owner), out var balance)
            && balance is not null)
        {
            return balance.Available;
        }
        // Transition fallback: Margin.Initial is deprecated under #107
        // slice 4 but kept here so legacy dogfood configs keep working
        // until a follow-up removes the property. The startup warning
        // in Program.cs nudges operators to migrate.
#pragma warning disable CS0618 // Type or member is obsolete
        return _options.CurrentValue.Margin.Initial.GetValueOrDefault(account.Owner, 0m);
#pragma warning restore CS0618
    }

    /// <summary>Test/observability helper: returns the currently reserved amount for an owner.</summary>
    internal decimal ReservedForTesting(string owner) =>
        _reserved.Where(kv => string.Equals(kv.Key.Owner, owner, StringComparison.Ordinal))
            .Sum(kv => kv.Value);

    internal decimal ReservedForTesting(string firmId, string owner) =>
        _reserved.GetValueOrDefault(MarginAccountKey.Create(firmId, owner), 0m);

    /// <summary>Test/observability helper: returns the currently available amount for an owner.</summary>
    internal decimal AvailableForTesting(string owner)
    {
        var account = _reserved.Keys.FirstOrDefault(
            key => string.Equals(key.Owner, owner, StringComparison.Ordinal));
        if (string.IsNullOrEmpty(account.FirmId))
            account = MarginAccountKey.Create(CashLedger.DefaultFirmId, owner);
        return ResolveBaseAvailable(account) - ReservedForTesting(owner);
    }

    internal decimal AvailableForTesting(string firmId, string owner)
    {
        var account = MarginAccountKey.Create(firmId, owner);
        return ResolveBaseAvailable(account) - _reserved.GetValueOrDefault(account, 0m);
    }

    /// <summary>
    /// Memory-growth observability for the suspended-reservation
    /// path (#153 follow-up). A reservation entry that flipped to
    /// <c>IsSuspended</c> stays in the dictionary until either an
    /// admin clear-stale fires <see cref="ExecKind.Restored"/>, the
    /// venue eventually delivers a terminal ER, or the host
    /// restarts. If the venue never recovers and admin never acts,
    /// those entries leak. Operators can watch the suspended count
    /// here (gauge <c>trading.risk.margin_reservations{state}</c>)
    /// to spot accumulation; the value should track the number of
    /// flagged-stale orders visible in the trader UI.
    /// </summary>
    public (int Active, int Suspended) GetReservationCounts()
    {
        // No lock needed: ConcurrentDictionary's enumerator is
        // weakly-consistent and a snapshot count is good enough for
        // an observability gauge sampled every few seconds.
        var suspended = 0;
        var active = 0;
        foreach (var kv in _reservations)
        {
            if (kv.Value.IsSuspended) suspended++;
            else active++;
        }
        return (active, suspended);
    }

    // ----- IReplaceMarginCoordinator (slice 2 of #122) -----

    /// <inheritdoc />
    public Task<RiskDecision> PrepareReplaceAsync(
        ulong originalClOrdId,
        ulong newClOrdId,
        EndClientId owner,
        decimal newRemainingNotional,
        CancellationToken ct)
    {
        var firmId = _reservations.TryGetValue(originalClOrdId, out var original)
            ? original.FirmId
            : CashLedger.DefaultFirmId;
        return PrepareReplaceAsync(
            originalClOrdId,
            newClOrdId,
            owner,
            firmId,
            newRemainingNotional,
            ct);
    }

    public Task<RiskDecision> PrepareReplaceAsync(
        ulong originalClOrdId,
        ulong newClOrdId,
        EndClientId owner,
        string firmId,
        decimal newRemainingNotional,
        CancellationToken ct)
    {
        // Margin globally disabled: the DI container points
        // IMarginProvider at the NoOp variant and never reserves on
        // submit. The replace coordinator, however, is always wired
        // to the concrete provider (so Commit/Abort can clean up if
        // margin gets toggled mid-session); short-circuit Prepare here
        // so it doesn't reject upsizes against an empty ledger.
        if (!_options.CurrentValue.Margin.Enabled)
            return Task.FromResult(RiskDecision.Approve);

        var ownerKey = owner.Value;
        var account = MarginAccountKey.Create(firmId, ownerKey);
        lock (_gate)
        {
            if (_reservations.TryGetValue(newClOrdId, out var existing))
            {
                if (existing.OriginalQty == 0
                    && string.Equals(existing.FirmId, firmId, StringComparison.Ordinal)
                    && string.Equals(existing.Owner, ownerKey, StringComparison.Ordinal))
                {
                    return Task.FromResult(RiskDecision.Approve);
                }
                return Task.FromResult(RiskDecision.Reject(
                    $"replace ClOrdID {newClOrdId} already has a non-replace reservation"));
            }

            // Sells / markets / non-positive notionals never touched the
            // reservation ledger on submit; track a zero-delta transient so
            // repeated prepare/recovery calls remain idempotent.
            if (newRemainingNotional <= 0m)
            {
                _reservations[newClOrdId] = new ReservationEntry(
                    firmId, ownerKey, 0m, 0L, 0m);
                return Task.FromResult(RiskDecision.Approve);
            }

            // #153. A suspended original held no cash in _reserved, so
            // the upsize-delta math must treat its tracked remaining as
            // zero. Otherwise we'd approve a replace that, at commit
            // time, restores the missing notional and pushes the owner
            // over their cap. Same predicate applied in CommitReplace
            // below — keep them in sync.
            decimal oldHeldRemaining = 0m;
            if (_reservations.TryGetValue(originalClOrdId, out var origEntry) && !origEntry.IsSuspended)
            {
                oldHeldRemaining = origEntry.RemainingNotional;
            }

            // Delta semantics: we only need to reserve *additional*
            // capacity when scaling up. Downsize / same / sell-side
            // replace requires no extra reserve at Prepare time;
            // Commit will rebalance to the venue-confirmed figure.
            var delta = newRemainingNotional - oldHeldRemaining;
            if (delta <= 0m)
            {
                // Track the in-flight intent with a zero-notional entry
                // so AbortReplace has something to remove and Commit
                // knows the Prepare ran. No effect on _reserved.
                _reservations[newClOrdId] = new ReservationEntry(
                    firmId, ownerKey, 0m, 0L, 0m);
                return Task.FromResult(RiskDecision.Approve);
            }

            var reserved = _reserved.GetValueOrDefault(account, 0m);
            var available = ResolveBaseAvailable(account) - reserved;
            if (delta > available)
            {
                return Task.FromResult(RiskDecision.Reject(
                    $"insufficient margin for replace upsize: delta {delta} exceeds available {available} for firm/end-client '{firmId}/{ownerKey}'"));
            }

            _reserved[account] = reserved + delta;
            // The transient reservation under newClOrdId carries only
            // the delta — Commit will top it up to confirmedRemainingNotional.
            _reservations[newClOrdId] = new ReservationEntry(
                firmId, ownerKey, 0m, 0L, delta);
            return Task.FromResult(RiskDecision.Approve);
        }
    }

    /// <inheritdoc />
    public void CommitReplace(
        ulong originalClOrdId,
        ulong newClOrdId,
        decimal confirmedRemainingNotional)
    {
        lock (_gate)
        {
            // Issue #247 / PR #248 P2. The Margin.Enabled gate must live
            // inside the lock and must NOT skip the cleanup path: the
            // toggle can flip live via IOptionsMonitor (admin reload),
            // and TryReserveAsync/PrepareReplaceAsync don't gate on
            // Margin.Enabled. So _reservations[orig] / [new] may exist
            // even when Margin is currently disabled — those slots must
            // still be released here, otherwise a mid-session disable
            // leaks every in-flight reservation.
            //
            // Remove the transient entry (set up by Prepare) — its
            // RemainingNotional is the upsize delta we already reserved
            // (or zero for downsize/same).
            decimal transientDelta = 0m;
            ReservationEntry? accountEntry = null;
            if (_reservations.TryRemove(newClOrdId, out var transient))
            {
                transientDelta = transient.RemainingNotional;
                accountEntry = transient;
            }

            // Release the original entry entirely (returns oldRemaining).
            // #153. As in PrepareReplaceAsync, a suspended original
            // held no cash in _reserved — the adjustment math must
            // not subtract its remaining notional, otherwise the
            // owner's reserved figure would be over-released.
            decimal oldRemaining = 0m;
            if (_reservations.TryRemove(originalClOrdId, out var origEntry))
            {
                if (!origEntry.IsSuspended)
                    oldRemaining = origEntry.RemainingNotional;
                accountEntry ??= origEntry;
            }

            if (accountEntry is null)
            {
                // No reservation existed on either side. If margin is
                // currently disabled, this is the legitimate "started
                // disabled, nothing was ever reserved" case — silent
                // no-op (don't spam logs on every modify-to-fill).
                if (!_options.CurrentValue.Margin.Enabled)
                    return;

                // Margin is enabled and yet neither side carried a
                // reservation: this is a real bug (not a config no-op),
                // because the matching PrepareReplaceAsync should have
                // populated the transient entry under newClOrdId. Most
                // likely culprit is a code path that registered the
                // intent without going through the coordinator. Surface
                // it as an error + counter so it's alertable.
                if (confirmedRemainingNotional > 0m)
                {
                    _logger.LogError(
                        "CommitReplace asked to track {Notional} for new ClOrdID {NewClOrdId} (orig {OrigClOrdId}) but neither original nor pending reservation has an owner; dropping. This indicates a Prepare/Commit mismatch — reservation will leak.",
                        confirmedRemainingNotional, newClOrdId, originalClOrdId);
                    MetricsRegistry.MarginCommitReplaceDropped.Add(1);
                }
                return;
            }

            // Net change to _reserved[owner]:
            //   release oldRemaining (-)
            //   release transientDelta (- because we'll re-add via newReserved)
            //   add confirmedRemainingNotional (+)
            // Combined: confirmedRemainingNotional - oldRemaining - transientDelta
            // (which equals zero for the upsize/same Prepare-then-Commit sequence
            // when the venue confirms the qty we asked for).
            var account = accountEntry.Account;
            var reserved = _reserved.GetValueOrDefault(account, 0m);
            var adjustment = confirmedRemainingNotional - oldRemaining - transientDelta;
            var next = reserved + adjustment;
            if (next < 0m)
            {
                _logger.LogWarning(
                    "CommitReplace adjustment for owner {Owner} would push reserved below zero ({Reserved} + {Adjustment}); clamping to zero.",
                    account.Owner, reserved, adjustment);
                next = 0m;
            }
            _reserved[account] = next;

            if (confirmedRemainingNotional > 0m)
            {
                _reservations[newClOrdId] =
                    new ReservationEntry(
                        account.FirmId,
                        account.Owner,
                        0m,
                        0L,
                        confirmedRemainingNotional);
            }
        }
    }

    /// <inheritdoc />
    public void AbortReplace(ulong newClOrdId)
    {
        // Releases the upsize delta only; original reservation untouched.
        lock (_gate)
        {
            if (_reservations.TryGetValue(newClOrdId, out var entry))
                ReleaseRemaining_Locked(newClOrdId, entry);
        }
    }

    private sealed record ReservationEntry(
        string FirmId,
        string Owner,
        decimal Price,
        long OriginalQty,
        decimal RemainingNotional,
        bool IsSuspended = false)
    {
        public MarginAccountKey Account => MarginAccountKey.Create(FirmId, Owner);

        // OPT-B (#484). Snapshot of the notional reserved at submit
        // (price * qty * multiplier). RemainingNotional shrinks with
        // partial releases; OriginalNotional stays put so the
        // per-contract release amount is derivable for partial fills.
        public decimal OriginalNotional { get; init; } = RemainingNotional;
    }

    private readonly record struct MarginAccountKey(string FirmId, string Owner)
    {
        public static MarginAccountKey Create(string firmId, string owner)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
            ArgumentException.ThrowIfNullOrWhiteSpace(owner);
            return new MarginAccountKey(firmId, owner);
        }
    }

    private sealed class MarginAccountKeyComparer : IEqualityComparer<MarginAccountKey>
    {
        public static readonly MarginAccountKeyComparer Instance = new();

        public bool Equals(MarginAccountKey x, MarginAccountKey y) =>
            string.Equals(x.FirmId, y.FirmId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Owner, y.Owner, StringComparison.Ordinal);

        public int GetHashCode(MarginAccountKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FirmId),
                StringComparer.Ordinal.GetHashCode(obj.Owner));
    }
}
