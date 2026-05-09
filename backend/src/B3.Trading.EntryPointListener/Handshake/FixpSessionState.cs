namespace B3.Trading.EntryPointListener.Handshake;

/// <summary>Per-connection FIXP session state machine states.</summary>
public enum FixpSessionState
{
    /// <summary>No session negotiated yet — initial state.</summary>
    Idle,

    /// <summary>Negotiate accepted; awaiting Establish.</summary>
    Negotiated,

    /// <summary>Establish accepted; application messages may flow.</summary>
    Established,

    /// <summary>Terminate sent or received; connection is closing.</summary>
    Terminated,
}
