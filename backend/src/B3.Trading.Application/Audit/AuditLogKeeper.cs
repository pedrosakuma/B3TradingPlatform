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

    public const string AdminConfigChange = "admin.config.change";
    public const string AdminSubAccountCreate = "admin.subaccount.create";
    public const string AdminSubAccountDeactivate = "admin.subaccount.deactivate";

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
/// from the WAL tail driven through <c>EventReplayer.Apply</c>.</para>
/// </summary>
public sealed class AuditLogKeeper
{
    private readonly object _lock = new();
    private readonly List<AuditEntry> _ring;
    private readonly int _capacity;

    public AuditLogKeeper(IOptions<AuditLogOptions> options)
    {
        var opts = options?.Value ?? new AuditLogOptions();
        _capacity = opts.Capacity > 0 ? opts.Capacity : 100_000;
        _ring = new List<AuditEntry>(Math.Min(_capacity, 1024));
    }

    /// <summary>
    /// Total number of entries currently retained. Exposed for tests
    /// and the eviction counter.
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _ring.Count; }
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
            _ring.Add(entry);
            if (_ring.Count > _capacity)
            {
                // Drop the oldest. RemoveAt(0) is O(n) but the cap is
                // small (100k by default) and admin reads dwarf the
                // append rate; the simple shape avoids a custom ring
                // index for what is essentially a forensic surface.
                _ring.RemoveAt(0);
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
        long? cursorSeq)
    {
        if (limit <= 0) limit = 100;
        if (limit > 500) limit = 500;

        var isPrefix = typePattern is not null && typePattern.EndsWith(".*", StringComparison.Ordinal);
        var typePrefix = isPrefix ? typePattern!.Substring(0, typePattern!.Length - 1) : null;

        var matches = new List<AuditEntry>(Math.Min(limit, 64));
        lock (_lock)
        {
            // Walk newest-first.
            for (var i = _ring.Count - 1; i >= 0; i--)
            {
                var e = _ring[i];
                if (cursorSeq is long c && e.Seq >= c) continue;
                if (e.TimestampUtc < since || e.TimestampUtc > until) continue;
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
                    var actorMatch = string.Equals(e.ActorUsername, user, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(e.ActorUserId, user, StringComparison.OrdinalIgnoreCase);
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
}

/// <summary>Result of <see cref="AuditLogKeeper.Query"/>.</summary>
public sealed record AuditQueryResult(IReadOnlyList<AuditEntry> Entries, long? NextCursorSeq);
