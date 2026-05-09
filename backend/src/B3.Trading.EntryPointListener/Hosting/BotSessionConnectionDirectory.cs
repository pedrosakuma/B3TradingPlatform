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
    private readonly ConcurrentDictionary<Guid, IBotSessionOutboundSender> _byCredentialId = new();

    public void Register(Guid credentialId, IBotSessionOutboundSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (credentialId == Guid.Empty)
            throw new ArgumentException("CredentialId must not be empty.", nameof(credentialId));
        // Last-write-wins. The caller (FixpSessionConnection) only invokes
        // this after the per-credential session slot has been claimed via
        // IUserBotSessionRegistry.TryClaimActiveAsync, so two concurrent
        // Register calls for the same credentialId imply a race the
        // session registry already serialised — the second one is the
        // legitimate owner.
        _byCredentialId[credentialId] = sender;
    }

    public void Deregister(Guid credentialId, IBotSessionOutboundSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        // Compare-and-swap semantics: only remove if we are still the
        // recorded sender. A newer connection that already replaced us
        // must not be evicted by a stale close from the prior socket.
        var kvp = new KeyValuePair<Guid, IBotSessionOutboundSender>(credentialId, sender);
        ((ICollection<KeyValuePair<Guid, IBotSessionOutboundSender>>)_byCredentialId).Remove(kvp);
    }

    public bool TryGet(Guid credentialId, out IBotSessionOutboundSender sender)
    {
        if (_byCredentialId.TryGetValue(credentialId, out var found))
        {
            sender = found;
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
        if (_byCredentialId.TryRemove(credentialId, out var sender))
        {
            if (sender is IDisposable d) d.Dispose();
            return true;
        }
        return false;
    }
}
