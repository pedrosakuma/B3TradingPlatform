using B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Trading.EntryPointListener.Handshake;

/// <summary>Discriminator for <see cref="HandshakeAction"/>.</summary>
internal enum HandshakeActionKind
{
    SendNegotiateResponse,
    SendEstablishAck,
    AckTerminateAndClose,
    Terminate,
    NoOp,
}

/// <summary>
/// Result of a state-machine transition — tells
/// <c>FixpSessionConnection</c> what to send back on the wire.
/// </summary>
internal readonly struct HandshakeAction
{
    private HandshakeAction(HandshakeActionKind kind, TerminationCode termCode = default)
    {
        Kind = kind;
        TermCode = termCode;
    }

    public HandshakeActionKind Kind { get; }

    /// <summary>Only meaningful when <see cref="Kind"/> is <see cref="HandshakeActionKind.Terminate"/>.</summary>
    public TerminationCode TermCode { get; }

    public bool IsTerminating =>
        Kind is HandshakeActionKind.AckTerminateAndClose or HandshakeActionKind.Terminate;

    public static readonly HandshakeAction SendNegotiateResponse =
        new(HandshakeActionKind.SendNegotiateResponse);

    public static readonly HandshakeAction SendEstablishAck =
        new(HandshakeActionKind.SendEstablishAck);

    public static readonly HandshakeAction AckTerminateAndClose =
        new(HandshakeActionKind.AckTerminateAndClose);

    public static readonly HandshakeAction NoOp =
        new(HandshakeActionKind.NoOp);

    public static HandshakeAction Terminate(TerminationCode code) =>
        new(HandshakeActionKind.Terminate, code);
}
