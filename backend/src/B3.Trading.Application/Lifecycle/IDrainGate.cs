namespace B3.Trading.Application.Lifecycle;

/// <summary>
/// Process-wide drain signal exposed to application-layer services. The
/// concrete implementation lives in the API layer (it ties into
/// <c>IHostApplicationLifetime.ApplicationStopping</c>); the application
/// layer only sees the read-side flag so the dependency arrow stays
/// Application → (nothing).
/// </summary>
public interface IDrainGate
{
    bool IsDraining { get; }
}
