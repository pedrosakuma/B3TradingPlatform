using B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Trading.EntryPointListener.Handshake;

/// <summary>
/// Pure state machine for the FIXP V6 session-control handshake.
/// One instance per connection; not thread-safe.
///
/// <para>Transitions per RFC §4.4:</para>
/// <list type="bullet">
///   <item>Idle      → Negotiated  on Negotiate (valid)</item>
///   <item>Negotiated → Established on Establish (SessionId/VerID match)</item>
///   <item>* → Terminated           on Terminate</item>
/// </list>
/// </summary>
internal sealed class FixpHandshakeStateMachine
{
    private FixpSessionState _state = FixpSessionState.Idle;
    private SessionID _sessionId;
    private SessionVerID _sessionVerId;

    public FixpSessionState State => _state;

    /// <summary>Session ID echoed from the client's Negotiate — valid after first successful <see cref="OnNegotiate"/>.</summary>
    public SessionID SessionId => _sessionId;

    /// <summary>Session version ID echoed from the client's Negotiate — valid after first successful <see cref="OnNegotiate"/>.</summary>
    public SessionVerID SessionVerId => _sessionVerId;

    public HandshakeAction OnNegotiate(in NegotiateData msg)
    {
        switch (_state)
        {
            case FixpSessionState.Negotiated:
            case FixpSessionState.Established:
                return HandshakeAction.Terminate(TerminationCode.NEGOTIATION_IN_PROGRESS);

            case FixpSessionState.Terminated:
                return HandshakeAction.Terminate(TerminationCode.UNSPECIFIED);

            default: // Idle
                _sessionId = msg.SessionID;
                _sessionVerId = msg.SessionVerID;
                _state = FixpSessionState.Negotiated;
                return HandshakeAction.SendNegotiateResponse;
        }
    }

    public HandshakeAction OnEstablish(in EstablishData msg)
    {
        switch (_state)
        {
            case FixpSessionState.Idle:
                return HandshakeAction.Terminate(TerminationCode.UNNEGOTIATED);

            case FixpSessionState.Established:
                return HandshakeAction.Terminate(TerminationCode.ESTABLISH_IN_PROGRESS);

            case FixpSessionState.Terminated:
                return HandshakeAction.Terminate(TerminationCode.UNSPECIFIED);

            default: // Negotiated
                if ((uint)msg.SessionID != (uint)_sessionId)
                    return HandshakeAction.Terminate(TerminationCode.INVALID_SESSIONID);
                if ((ulong)msg.SessionVerID != (ulong)_sessionVerId)
                    return HandshakeAction.Terminate(TerminationCode.INVALID_SESSIONVERID);
                _state = FixpSessionState.Established;
                return HandshakeAction.SendEstablishAck;
        }
    }

    public HandshakeAction OnTerminate(in TerminateData msg)
    {
        _ = msg.SessionID; // documented but not validated in stub; sub-issue C adds auth checks
        _state = FixpSessionState.Terminated;
        return HandshakeAction.AckTerminateAndClose;
    }

    /// <summary>
    /// Called when an application-layer message arrives before the session
    /// is established.  Always terminates with UNNEGOTIATED.
    /// </summary>
    public HandshakeAction OnApplicationMessageBeforeEstablished()
        => HandshakeAction.Terminate(TerminationCode.UNNEGOTIATED);
}
