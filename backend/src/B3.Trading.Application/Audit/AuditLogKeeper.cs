using System.Collections.Generic;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Audit;

/// <summary>
/// Q4.5 (#305). Outcome class for an <see cref="AuditEntry"/>. Stored on
/// the WAL as the lowercase string the read-path filter expects.
/// </summary>
public static class AuditOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Denied = "denied";
}

/// <summary>
/// Q4.5 (#305). Canonical event-type strings emitted by the platform.
/// Centralised so the capture sites + tests + ops dashboards agree
/// on the wire spelling. Strings (not an enum) so the WAL stays
/// open-set — new capture sites can land without bumping the
/// snapshot schema.
/// </summary>
public static class AuditEventTypes
{
    public const string AuthLoginSuccess = "auth.login.success";
    public const string AuthLoginFailure = "auth.login.failure";
    public const string AuthTwoFactorEnrollStart = "auth.2fa.enroll.start";
    public const string AuthTwoFactorEnrollConfirm = "auth.2fa.enroll.confirm";
    public const string AuthTwoFactorVerifySuccess = "auth.2fa.verify.success";
    public const string AuthTwoFactorVerifyFailure = "auth.2fa.verify.failure";
    public const string AuthTwoFactorDisable = "auth.2fa.disable";
    public const string AuthTwoFactorRecoveryCodeConsumed = "auth.2fa.recovery_code_consumed";
    public const string AuthExchangeSuccess = "auth.exchange.success";
    public const string AuthExchangeFailure = "auth.exchange.failure";
    public const string IdentityBindingCreate = "identity.binding.create";
    public const string IdentityBindingDelete = "identity.binding.delete";
    public const string IdentityUserStatusChange = "identity.user.status_change";
    public const string IdentityUserAuthorizationChange = "identity.user.authorization_change";

    public const string AdminConfigChange = "admin.config.change";
    public const string AdminSubAccountCreate = "admin.subaccount.create";
    public const string AdminSubAccountDeactivate = "admin.subaccount.deactivate";
    public const string AdminOutboundResolution = "admin.outbound_resolution";

    /// <summary>Q4.6 (#306). Drop-copy WS session opened by a compliance / admin principal.</summary>
    public const string DropCopyConnect = "audit.dropcopy.connect";

    /// <summary>Q4.6 (#306). Drop-copy WS session closed (clean or peer-gone).</summary>
    public const string DropCopyDisconnect = "audit.dropcopy.disconnect";

    /// <summary>Q4.8 (#308). CVM 35/505 transaction report downloaded by a compliance/admin principal.</summary>
    public const string ReportCvmDownload = "report.cvm.download";

    /// <summary>#679. Self-service sandbox cash deposit via <c>POST /balance/deposit</c> (as opposed to operator-driven <c>admin.config.change</c> via <c>/admin/cash</c>).</summary>
    public const string SandboxCashSelfDeposit = "sandbox.cash.self_deposit";

    // Prefix helpers used by the read-path filter for type=auth.* style globs.
    public const string AuthPrefix = "auth.";
    public const string AdminPrefix = "admin.";
}

/// <summary>
/// Q4.5 (#305). Read-side projection of <see cref="AuditLogEvent"/>:
/// the WAL envelope plus the monotonic sequence number assigned at
/// append time. Used as the API response shape for <c>GET
/// /admin/audit</c> and as the opaque-cursor anchor (the cursor is a
/// base64-encoded packing of <see cref="Seq"/> + <see cref="TimestampUtc"/>).
/// </summary>
public sealed record AuditEntry(
    long Seq,
    string Id,
    DateTimeOffset TimestampUtc,
    string EventType,
    string Outcome,
    string? ActorUserId,
    string? ActorUsername,
    string? ActorFirm,
    string? ActorRole,
    string? SourceIp,
    string? ResourcePath,
    string? ReasonCode,
    IReadOnlyDictionary<string, string>? Details);

/// <summary>
/// Q4.5 (#305). Bounded in-memory ring-buffer projection of every
/// <see cref="AuditLogEvent"/> that lands on the WAL. Backs
/// <c>GET /admin/audit</c>. Append is synchronous and called from
/// both the live capture path (under the dispatcher lock, via
/// <see cref="EventDispatcher.Dispatch(WalEvent, System.Action)"/>)
/// and the recovery replayer (single-threaded). Reads are taken
/// under a lock to give the API a stable, time-ordered snapshot of
/// the buffer.
///
/// <para>The keeper is intentionally <i>not</i> projected into the
/// platform snapshot: audit history is naturally append-only and
/// time-bounded, the WAL is the source of truth, and a snapshot
/// field would just duplicate it. On restart the keeper rehydrates
/// from the <b>full WAL</b> driven by
/// <see cref="Infrastructure.Persistence.PersistenceRecovery"/> —
/// not just the post-snapshot tail. Pass-1 review (#322) P1.1: an
/// older design replayed only from <c>snapshot.Seq</c>, which
/// silently dropped every audit event captured before the latest
/// snapshot on cold restart. The recovery driver now does two
/// passes against <see cref="Application.Persistence.IEventStore.ReadFromAsync"/>:
/// an audit-only pre-pass for <c>seq &lt;= snapshot.Seq</c> that
/// folds historic audit envelopes into this keeper, then the main
/// snapshot+tail replay. Cost is O(N) where N is total WAL events
/// — the bounded ring (default <see cref="AuditLogOptions.Capacity"/>
/// = 100k) caps in-memory occupancy; older entries silently fall
/// off the head as the pre-pass scans forward.</para>
/// </summary>
public sealed class AuditLogKeeper
{
    private readonly object _lock = new();
    // True circular buffer. `_ring` is lazily grown up to `_capacity`,
    // then becomes a fixed-size wrap-around array indexed by `_head`
    // (the next write slot). Pass-2 review (#322) P2: an earlier
    // List<T>+RemoveAt(0) eviction was O(capacity) per evicted entry,
    // turning the recovery pre-pass into O(N · capacity) once N
    // exceeded the cap. The head-indexed ring evicts in O(1) and keeps
    // both append and recovery linear in N.
    private AuditEntry?[] _ring;
    private int _head;
    private int _count;
    private readonly int _capacity;

    public AuditLogKeeper(IOptions<AuditLogOptions> options)
    {
        var opts = options?.Value ?? new AuditLogOptions();
        _capacity = opts.Capacity > 0 ? opts.Capacity : 100_000;
        _ring = new AuditEntry?[Math.Min(_capacity, 1024)];
    }

    /// <summary>
    /// Total number of entries currently retained. Exposed for tests
    /// and the eviction counter.
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public int Capacity => _capacity;

    /// <summary>
    /// Folds a WAL envelope onto the buffer. Called by both live
    /// dispatch and recovery replay. <paramref name="seq"/> is the
    /// WAL seq assigned at append time. Older entries are silently
    /// dropped once <see cref="Capacity"/> is reached.
    /// </summary>
    public void Apply(long seq, AuditLogEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var entry = new AuditEntry(
            Seq: seq,
            Id: Guid.NewGuid().ToString("N"),
            TimestampUtc: evt.TimestampUtc,
            EventType: evt.EventType,
            Outcome: evt.Outcome,
            ActorUserId: evt.ActorUserId,
            ActorUsername: evt.ActorUsername,
            ActorFirm: evt.ActorFirm,
            ActorRole: evt.ActorRole,
            SourceIp: evt.SourceIp,
            ResourcePath: evt.ResourcePath,
            ReasonCode: evt.ReasonCode,
            Details: evt.Details is null ? null : new Dictionary<string, string>(evt.Details));

        lock (_lock)
        {
            if (_count < _capacity)
            {
                // Pre-cap: grow the backing array geometrically up to
                // the cap so initial sparse usage doesn't allocate the
                // full bound. `_head` may already have wrapped to 0 by
                // the time we reach the grow trigger (head advances
                // modulo ring length, so a sequential 0..len-1 fill
                // leaves head==0). After Array.Copy the entries occupy
                // physical slots 0.._count-1 in logical order, so the
                // next append must go to slot _count. Pass-3 review
                // (#322) caught the missing reset: without it the new
                // entry overwrites slot 0 and slots _count..len-1
                // stay null, crashing Query with NRE at the first
                // walk past the original ring length.
                if (_count == _ring.Length)
                {
                    var newLen = Math.Min(_capacity, Math.Max(_ring.Length * 2, 16));
                    var grown = new AuditEntry?[newLen];
                    Array.Copy(_ring, grown, _count);
                    _ring = grown;
                    _head = _count;
                }
                _ring[_head] = entry;
                _head = (_head + 1) % _ring.Length;
                _count++;
            }
            else
            {
                // Steady-state wrap: overwrite the oldest slot (the one
                // `_head` currently points at) in O(1) and advance head.
                _ring[_head] = entry;
                _head = (_head + 1) % _capacity;
            }
        }
    }

    /// <summary>
    /// Returns matching entries in newest-first order, paginated by an
    /// opaque cursor. The cursor anchors on a previously returned
    /// <see cref="AuditEntry.Seq"/>: callers see entries with
    /// <c>Seq &lt; cursorSeq</c>.
    ///
    /// <para>Filters are AND-composed. <paramref name="user"/> matches
    /// <see cref="AuditEntry.ActorUsername"/> OR the
    /// <c>"target_user"</c> detail (so role/2FA changes targeting
    /// that user surface). <paramref name="typePattern"/> is exact
    /// match unless it ends with <c>".*"</c>, in which case it is a
    /// prefix glob.</para>
    /// </summary>
    public AuditQueryResult Query(
        DateTimeOffset since,
        DateTimeOffset until,
        string? user,
        string? typePattern,
        string? outcome,
        int limit,
        long? cursorSeq,
        string? firmFilter = null,
        bool restrictUserToFirm = false)
    {
        if (limit <= 0) limit = 100;
        if (limit > 500) limit = 500;

        var isPrefix = typePattern is not null && typePattern.EndsWith(".*", StringComparison.Ordinal);
        var typePrefix = isPrefix ? typePattern!.Substring(0, typePattern!.Length - 1) : null;

        var matches = new List<AuditEntry>(Math.Min(limit, 64));
        lock (_lock)
        {
            // Walk newest-first. Newest entry is at (_head - 1) mod
            // ring length; walk backwards `_count` slots.
            var ringLen = _ring.Length;
            var idx = (_head - 1 + ringLen) % ringLen;
            for (var n = 0; n < _count; n++, idx = (idx - 1 + ringLen) % ringLen)
            {
                var e = _ring[idx]!;
                if (cursorSeq is long c && e.Seq >= c) continue;
                if (e.TimestampUtc < since || e.TimestampUtc > until) continue;
                // Q4.14 (#314). Optional firm-scope filter. When non-null,
                // an entry survives if it touches the firm — either the
                // actor was in that firm OR the action targeted that firm
                // (a well-known Details key carries the target firm id).
                // Used to restrict compliance principals to their own firm
                // at /admin/audit; never trusted from the query string.
                //
                // Pass-1 review (#327) P1.1: previously matched only on
                // ActorFirm, which (a) leaked cross-firm targets when an
                // admin in the compliance user's firm operated on another
                // firm, and (b) hid the audit trail of cross-firm admin
                // actions against the compliance user's own firm. Matching
                // on target-firm details closes both gaps. The endpoint
                // applies an additional redaction pass over the surviving
                // entries before serializing.
                if (!string.IsNullOrEmpty(firmFilter)
                    && !FirmTouchesEntry(e, firmFilter))
                    continue;
                if (!string.IsNullOrEmpty(outcome) && !string.Equals(e.Outcome, outcome, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (typePattern is not null)
                {
                    if (isPrefix)
                    {
                        if (!e.EventType.StartsWith(typePrefix!, StringComparison.Ordinal)) continue;
                    }
                    else
                    {
                        if (!string.Equals(e.EventType, typePattern, StringComparison.Ordinal)) continue;
                    }
                }
                if (!string.IsNullOrEmpty(user))
                {
                    // Pass-3 (#327): when restrictUserToFirm is set
                    // (compliance scope), only probe actor identity on
                    // entries actually authored within the caller's
                    // firm. Otherwise a compliance caller could guess
                    // foreign usernames via ?user= and learn whether
                    // they performed any action touching this firm,
                    // even though the surfaced entry would later have
                    // ActorUsername/ActorUserId redacted.
                    var actorEligible = !restrictUserToFirm
                        || (firmFilter is not null
                            && string.Equals(e.ActorFirm, firmFilter, StringComparison.OrdinalIgnoreCase));
                    var actorMatch = actorEligible
                                   && (string.Equals(e.ActorUsername, user, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(e.ActorUserId, user, StringComparison.OrdinalIgnoreCase));
                    var targetMatch = e.Details is not null
                        && e.Details.TryGetValue("target_user", out var tu)
                        && string.Equals(tu, user, StringComparison.OrdinalIgnoreCase);
                    if (!actorMatch && !targetMatch) continue;
                }

                matches.Add(e);
                if (matches.Count >= limit) break;
            }
        }

        long? next = matches.Count == limit ? matches[^1].Seq : (long?)null;
        return new AuditQueryResult(matches, next);
    }

    /// <summary>
    /// Q4.14 (#314). Well-known Details keys that carry a firm
    /// identifier — used by the compliance scope to admit entries
    /// where the actor was in another firm but the action targeted
    /// the compliance caller's firm (and, dually, by the endpoint
    /// to redact other-firm names from surviving entries).
    ///
    /// <para>Pass-2 review (#327): every new audit emission site that
    /// stores a firm id in Details MUST use one of these keys (rather
    /// than inventing a new one) so the compliance audit projection
    /// stays leak-tight. <c>firmIdViewed</c> was added for the
    /// admin-overridden drop-copy WebSocket open/close pair.</para>
    /// </summary>
    public static readonly string[] FirmDetailKeys = new[] { "firm", "firmId", "firm_id", "target_firm", "firmIdViewed" };

    private static bool FirmTouchesEntry(AuditEntry e, string firm)
    {
        if (string.Equals(e.ActorFirm, firm, StringComparison.OrdinalIgnoreCase))
            return true;
        if (e.Details is null) return false;
        foreach (var key in FirmDetailKeys)
        {
            if (e.Details.TryGetValue(key, out var v)
                && string.Equals(v, firm, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>Result of <see cref="AuditLogKeeper.Query"/>.</summary>
public sealed record AuditQueryResult(IReadOnlyList<AuditEntry> Entries, long? NextCursorSeq);
