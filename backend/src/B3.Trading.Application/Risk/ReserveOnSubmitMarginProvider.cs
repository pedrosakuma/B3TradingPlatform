using System.Collections.Concurrent;
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
/// State is in-process, ephemeral, and not concurrency-safe across
/// processes — same posture as <see cref="PositionKeeper"/>. ER replay
/// after reconnect rebuilds positions; reservations for in-flight
/// orders that crossed a restart are abandoned with the order itself
/// (no orphaned holds because the reservation lives only as long as
/// the working order).
/// </para>
///
/// <para>
/// <b>Cash source (slice 2 of #107):</b> when a <see cref="CashLedger"/>
/// is wired in, the per-owner base capacity is read from the ledger's
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

    private readonly ConcurrentDictionary<ulong, ReservationEntry> _reservations = new();
    private readonly ConcurrentDictionary<string, decimal> _reserved =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ReserveOnSubmitMarginProvider(
        IOptionsMonitor<RiskOptions> options,
        ILogger<ReserveOnSubmitMarginProvider> logger,
        CashLedger? cash = null)
    {
        _options = options;
        _logger = logger;
        _cash = cash;
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

        var notional = ctx.Price.Value * ctx.Quantity;
        if (notional <= 0m)
            return Task.FromResult(RiskDecision.Approve);

        var owner = ctx.Owner.Value;
        var baseAvailable = ResolveBaseAvailable(owner);

        // Atomic check+reserve: take the gate, snapshot reserved,
        // verify capacity, mutate. The provider is a singleton so the
        // gate scope covers all races for the same owner.
        lock (_gate)
        {
            var reserved = _reserved.GetValueOrDefault(owner, 0m);
            var available = baseAvailable - reserved;
            if (notional > available)
            {
                return Task.FromResult(RiskDecision.Reject(
                    $"insufficient margin: notional {notional} exceeds available {available} for end-client '{owner}'"));
            }
            _reserved[owner] = reserved + notional;
            _reservations[clOrdId] = new ReservationEntry(owner, ctx.Price.Value, ctx.Quantity, notional);
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
                        DecrementReserved(entry.Owner, entry.RemainingNotional);
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
                        var current = _reserved.GetValueOrDefault(entry.Owner, 0m);
                        var next = current + entry.RemainingNotional;
                        var baseCap = ResolveBaseAvailable(entry.Owner);
                        if (next > baseCap)
                        {
                            _logger.LogWarning(
                                "Margin restore for {ClOrdId} overcommits owner {Owner}: reserved {Current} + restored {Restored} > base {Base}.",
                                clOrdId, entry.Owner, current, entry.RemainingNotional, baseCap);
                            MetricsRegistry.MarginOvercommitOnRestore.Add(
                                1, new KeyValuePair<string, object?>("owner", entry.Owner));
                        }
                        _reserved[entry.Owner] = next;
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

    private void ReleasePartial_Locked(ulong clOrdId, ReservationEntry entry, long lastQty)
    {
        var amount = entry.Price * lastQty;
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
            DecrementReserved(entry.Owner, amount);
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
            DecrementReserved(entry.Owner, entry.RemainingNotional);
        }
    }

    private void DecrementReserved(string owner, decimal amount)
    {
        var reserved = _reserved.GetValueOrDefault(owner, 0m);
        var next = reserved - amount;
        if (next < 0m) next = 0m;
        _reserved[owner] = next;
    }

    /// <summary>
    /// Resolves the per-owner base capacity that the reservation ledger
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
    private decimal ResolveBaseAvailable(string owner)
    {
        if (_cash is not null && _cash.TryGet(new EndClientId(owner), out var balance) && balance is not null)
        {
            return balance.Available;
        }
        // Transition fallback: Margin.Initial is deprecated under #107
        // slice 4 but kept here so legacy dogfood configs keep working
        // until a follow-up removes the property. The startup warning
        // in Program.cs nudges operators to migrate.
#pragma warning disable CS0618 // Type or member is obsolete
        return _options.CurrentValue.Margin.Initial.GetValueOrDefault(owner, 0m);
#pragma warning restore CS0618
    }

    /// <summary>Test/observability helper: returns the currently reserved amount for an owner.</summary>
    internal decimal ReservedForTesting(string owner) => _reserved.GetValueOrDefault(owner, 0m);

    /// <summary>Test/observability helper: returns the currently available amount for an owner.</summary>
    internal decimal AvailableForTesting(string owner) =>
        ResolveBaseAvailable(owner) - ReservedForTesting(owner);

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
        // Margin globally disabled: the DI container points
        // IMarginProvider at the NoOp variant and never reserves on
        // submit. The replace coordinator, however, is always wired
        // to the concrete provider (so Commit/Abort can clean up if
        // margin gets toggled mid-session); short-circuit Prepare here
        // so it doesn't reject upsizes against an empty ledger.
        if (!_options.CurrentValue.Margin.Enabled)
            return Task.FromResult(RiskDecision.Approve);

        // Sells / markets / non-positive notionals never touched the
        // reservation ledger on submit; they don't here either.
        if (newRemainingNotional <= 0m)
            return Task.FromResult(RiskDecision.Approve);

        var ownerKey = owner.Value;
        lock (_gate)
        {
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
                _reservations[newClOrdId] = new ReservationEntry(ownerKey, 0m, 0L, 0m);
                return Task.FromResult(RiskDecision.Approve);
            }

            var reserved = _reserved.GetValueOrDefault(ownerKey, 0m);
            var available = ResolveBaseAvailable(ownerKey) - reserved;
            if (delta > available)
            {
                return Task.FromResult(RiskDecision.Reject(
                    $"insufficient margin for replace upsize: delta {delta} exceeds available {available} for end-client '{ownerKey}'"));
            }

            _reserved[ownerKey] = reserved + delta;
            // The transient reservation under newClOrdId carries only
            // the delta — Commit will top it up to confirmedRemainingNotional.
            _reservations[newClOrdId] = new ReservationEntry(ownerKey, 0m, 0L, delta);
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
            string? owner = null;
            if (_reservations.TryRemove(newClOrdId, out var transient))
            {
                transientDelta = transient.RemainingNotional;
                owner = transient.Owner;
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
                owner ??= origEntry.Owner;
            }

            if (owner is null)
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
            var reserved = _reserved.GetValueOrDefault(owner, 0m);
            var adjustment = confirmedRemainingNotional - oldRemaining - transientDelta;
            var next = reserved + adjustment;
            if (next < 0m)
            {
                _logger.LogWarning(
                    "CommitReplace adjustment for owner {Owner} would push reserved below zero ({Reserved} + {Adjustment}); clamping to zero.",
                    owner, reserved, adjustment);
                next = 0m;
            }
            _reserved[owner] = next;

            if (confirmedRemainingNotional > 0m)
            {
                _reservations[newClOrdId] =
                    new ReservationEntry(owner, 0m, 0L, confirmedRemainingNotional);
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

    private sealed record ReservationEntry(string Owner, decimal Price, long OriginalQty, decimal RemainingNotional, bool IsSuspended = false);
}
