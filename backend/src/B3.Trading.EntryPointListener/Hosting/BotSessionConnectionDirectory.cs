using System.Collections.Concurrent;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// In-process implementation of <see cref="IBotSessionConnectionDirectory"/>.
/// A future multi-host deployment would need a distributed registration
/// (out of scope for v0 — single-active enforcement is also in-process,
/// see <c>IUserBotSessionRegistry</c>).
/// </summary>
public sealed class BotSessionConnectionDirectory : IBotSessionConnectionDirectory
{
    private sealed record ConnectionLease(
        string? ConnectionId,
        IBotSessionOutboundSender Sender);

    private readonly ConcurrentDictionary<Guid, ConnectionLease> _byCredentialId = new();

    public void Register(
        Guid credentialId,
        string connectionId,
        IBotSessionOutboundSender sender)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        RegisterCore(credentialId, connectionId, sender);
    }

    public void Register(Guid credentialId, IBotSessionOutboundSender sender) =>
        RegisterCore(credentialId, connectionId: null, sender);

    private void RegisterCore(
        Guid credentialId,
        string? connectionId,
        IBotSessionOutboundSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (credentialId == Guid.Empty)
            throw new ArgumentException("CredentialId must not be empty.", nameof(credentialId));
        ConnectionLease? displaced = null;
        var replacement = new ConnectionLease(connectionId, sender);
        _byCredentialId.AddOrUpdate(
            credentialId,
            replacement,
            (_, existing) =>
            {
                if (!ReferenceEquals(existing.Sender, sender))
                    displaced = existing;
                return replacement;
            });

        // The dictionary swap is the publication fence: once Register
        // returns, routing can only discover the replacement. Close the
        // displaced connection immediately so it cannot keep accepting
        // inbound orders after losing the lease.
        if (displaced?.Sender is IDisposable disposable)
            disposable.Dispose();
    }

    public void Deregister(Guid credentialId, IBotSessionOutboundSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        // Compare-and-swap semantics: only remove if we are still the
        // recorded sender. A newer connection that already replaced us
        // must not be evicted by a stale close from the prior socket.
        if (_byCredentialId.TryGetValue(credentialId, out var lease)
            && ReferenceEquals(lease.Sender, sender))
        {
            var kvp = new KeyValuePair<Guid, ConnectionLease>(credentialId, lease);
            ((ICollection<KeyValuePair<Guid, ConnectionLease>>)_byCredentialId).Remove(kvp);
        }
    }

    public bool TryGet(Guid credentialId, out IBotSessionOutboundSender sender)
    {
        if (_byCredentialId.TryGetValue(credentialId, out var found))
        {
            sender = found.Sender;
            return true;
        }
        sender = null!;
        return false;
    }

    /// <summary>Number of currently registered active sessions.</summary>
    public int ActiveCount => _byCredentialId.Count;

    /// <summary>Returns all registered credential IDs (for admin enumeration).</summary>
    public ICollection<Guid> RegisteredCredentialIds => _byCredentialId.Keys;

    /// <summary>
    /// Looks up the connection for <paramref name="credentialId"/> and
    /// disposes it (force-terminate). Returns <c>true</c> when found.
    /// </summary>
    public bool TryForceTerminate(Guid credentialId)
    {
        if (_byCredentialId.TryRemove(credentialId, out var lease))
        {
            if (lease.Sender is IDisposable d) d.Dispose();
            return true;
        }
        return false;
    }

    public bool TryForceTerminate(Guid credentialId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        while (_byCredentialId.TryGetValue(credentialId, out var lease))
        {
            if (!string.Equals(
                    lease.ConnectionId, connectionId, StringComparison.Ordinal))
                return false;
            var kvp = new KeyValuePair<Guid, ConnectionLease>(
                credentialId, lease);
            if (!((ICollection<KeyValuePair<Guid, ConnectionLease>>)_byCredentialId)
                    .Remove(kvp))
                continue;
            if (lease.Sender is IDisposable disposable)
                disposable.Dispose();
            return true;
        }
        return false;
    }
}
