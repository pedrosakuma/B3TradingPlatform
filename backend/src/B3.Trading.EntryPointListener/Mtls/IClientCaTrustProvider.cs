namespace B3.Trading.EntryPointListener.Mtls;

/// <summary>
/// Supplies the current <see cref="ClientCaTrustSnapshot"/> (trust anchors +
/// deny-list) for client-certificate validation. The snapshot is hot-reloaded
/// (RFC user-bot-fixp-mtls-v0 §5.2) so reading <see cref="Current"/> per
/// handshake always observes the latest CA bundle / revocation state without
/// a listener restart.
/// </summary>
public interface IClientCaTrustProvider
{
    /// <summary>The current, atomically-swapped trust snapshot.</summary>
    ClientCaTrustSnapshot Current { get; }
}
