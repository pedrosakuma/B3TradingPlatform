using System.Collections.Concurrent;
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
        if (!_reservations.TryGetValue(clOrdId, out var entry))
            return; // unknown order (Sell, Market, or never reserved here)

        switch (kind)
        {
            case ExecKind.PartialFill:
                if (lastQty > 0) ReleasePartial(clOrdId, entry, lastQty);
                break;

            case ExecKind.Fill:
                if (lastQty > 0) ReleasePartial(clOrdId, entry, lastQty);
                ReleaseRemaining(clOrdId);
                break;

            case ExecKind.Canceled:
            case ExecKind.Rejected:
                ReleaseRemaining(clOrdId);
                break;

                // New / Replaced: nothing to release. Replaced is handled
                // by the gateway re-issuing under a fresh ClOrdID; the
                // original reservation stays with the original ID.
        }
    }

    public void ReleaseReservation(ulong clOrdId) => ReleaseRemaining(clOrdId);

    private void ReleasePartial(ulong clOrdId, ReservationEntry entry, long lastQty)
    {
        var amount = entry.Price * lastQty;
        if (amount <= 0m) return;
        lock (_gate)
        {
            // Update the per-clordid entry so a subsequent partial
            // releases against the still-reserved remainder, never the
            // original.
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
            DecrementReserved(entry.Owner, amount);
            _reservations[clOrdId] = entry with { RemainingNotional = newRemaining };
        }
    }

    private void ReleaseRemaining(ulong clOrdId)
    {
        if (!_reservations.TryRemove(clOrdId, out var entry)) return;
        if (entry.RemainingNotional <= 0m) return;
        lock (_gate)
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
            var oldRemaining = _reservations.TryGetValue(originalClOrdId, out var origEntry)
                ? origEntry.RemainingNotional
                : 0m;

            // Delta semantics: we only need to reserve *additional*
            // capacity when scaling up. Downsize / same / sell-side
            // replace requires no extra reserve at Prepare time;
            // Commit will rebalance to the venue-confirmed figure.
            var delta = newRemainingNotional - oldRemaining;
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
            decimal oldRemaining = 0m;
            if (_reservations.TryRemove(originalClOrdId, out var origEntry))
            {
                oldRemaining = origEntry.RemainingNotional;
                owner ??= origEntry.Owner;
            }

            if (owner is null)
            {
                // No reservation existed on either side (sell or
                // never-reserved). Nothing to track going forward.
                if (confirmedRemainingNotional > 0m)
                {
                    _logger.LogWarning(
                        "CommitReplace asked to track {Notional} for new ClOrdID {NewClOrdId} but neither original nor pending reservation has an owner; dropping.",
                        confirmedRemainingNotional, newClOrdId);
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
        ReleaseRemaining(newClOrdId);
    }

    private sealed record ReservationEntry(string Owner, decimal Price, long OriginalQty, decimal RemainingNotional);
}
