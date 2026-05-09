using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.EntryPointListener.Handshake;

namespace B3.Trading.EntryPointListener.Tests.Handshake;

public class FixpHandshakeStateMachineTests
{
    private static NegotiateData MakeNegotiate(uint sessionId = 1, ulong sessionVerId = 1)
        => new() { SessionID = (SessionID)sessionId, SessionVerID = (SessionVerID)sessionVerId };

    private static EstablishData MakeEstablish(uint sessionId = 1, ulong sessionVerId = 1)
        => new() { SessionID = (SessionID)sessionId, SessionVerID = (SessionVerID)sessionVerId };

    private static TerminateData MakeTerminate(uint sessionId = 1, ulong sessionVerId = 1)
        => new() { SessionID = (SessionID)sessionId, SessionVerID = (SessionVerID)sessionVerId, TerminationCode = TerminationCode.FINISHED };

    // ─── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public void HappyPath_Idle_Negotiated_Established_Terminated()
    {
        var sm = new FixpHandshakeStateMachine();
        Assert.Equal(FixpSessionState.Idle, sm.State);

        var neg = MakeNegotiate(sessionId: 42, sessionVerId: 7);
        var a1 = sm.OnNegotiate(in neg);
        Assert.Equal(HandshakeActionKind.SendNegotiateResponse, a1.Kind);
        Assert.Equal(FixpSessionState.Negotiated, sm.State);

        var est = MakeEstablish(sessionId: 42, sessionVerId: 7);
        var a2 = sm.OnEstablish(in est);
        Assert.Equal(HandshakeActionKind.SendEstablishAck, a2.Kind);
        Assert.Equal(FixpSessionState.Established, sm.State);

        var term = MakeTerminate(sessionId: 42, sessionVerId: 7);
        var a3 = sm.OnTerminate(in term);
        Assert.Equal(HandshakeActionKind.AckTerminateAndClose, a3.Kind);
        Assert.Equal(FixpSessionState.Terminated, sm.State);
    }

    // ─── Error paths ──────────────────────────────────────────────────────────

    [Fact]
    public void EstablishBeforeNegotiate_ReturnsUnnegotiated()
    {
        var sm = new FixpHandshakeStateMachine();
        var est = MakeEstablish();
        var action = sm.OnEstablish(in est);
        Assert.Equal(HandshakeActionKind.Terminate, action.Kind);
        Assert.Equal(TerminationCode.UNNEGOTIATED, action.TermCode);
    }

    [Fact]
    public void DoubleEstablish_ReturnsEstablishInProgress()
    {
        var sm = new FixpHandshakeStateMachine();
        var neg = MakeNegotiate();
        sm.OnNegotiate(in neg);

        var est = MakeEstablish();
        sm.OnEstablish(in est);
        Assert.Equal(FixpSessionState.Established, sm.State);

        var est2 = MakeEstablish();
        var action = sm.OnEstablish(in est2);
        Assert.Equal(HandshakeActionKind.Terminate, action.Kind);
        Assert.Equal(TerminationCode.ESTABLISH_IN_PROGRESS, action.TermCode);
    }

    [Fact]
    public void DoubleNegotiate_ReturnsNegotiationInProgress()
    {
        var sm = new FixpHandshakeStateMachine();
        var neg = MakeNegotiate();
        sm.OnNegotiate(in neg);
        Assert.Equal(FixpSessionState.Negotiated, sm.State);

        var neg2 = MakeNegotiate();
        var action = sm.OnNegotiate(in neg2);
        Assert.Equal(HandshakeActionKind.Terminate, action.Kind);
        Assert.Equal(TerminationCode.NEGOTIATION_IN_PROGRESS, action.TermCode);
    }

    [Fact]
    public void DoubleNegotiate_AfterEstablish_ReturnsNegotiationInProgress()
    {
        var sm = new FixpHandshakeStateMachine();
        var neg = MakeNegotiate();
        sm.OnNegotiate(in neg);
        var est = MakeEstablish();
        sm.OnEstablish(in est);

        var neg2 = MakeNegotiate();
        var action = sm.OnNegotiate(in neg2);
        Assert.Equal(HandshakeActionKind.Terminate, action.Kind);
        Assert.Equal(TerminationCode.NEGOTIATION_IN_PROGRESS, action.TermCode);
    }

    [Fact]
    public void MismatchedSessionId_OnEstablish_ReturnsInvalidSessionId()
    {
        var sm = new FixpHandshakeStateMachine();
        var neg = MakeNegotiate(sessionId: 100, sessionVerId: 1);
        sm.OnNegotiate(in neg);

        var est = MakeEstablish(sessionId: 999, sessionVerId: 1); // wrong sessionId
        var action = sm.OnEstablish(in est);
        Assert.Equal(HandshakeActionKind.Terminate, action.Kind);
        Assert.Equal(TerminationCode.INVALID_SESSIONID, action.TermCode);
    }

    [Fact]
    public void MismatchedSessionVerId_OnEstablish_ReturnsInvalidSessionVerId()
    {
        var sm = new FixpHandshakeStateMachine();
        var neg = MakeNegotiate(sessionId: 100, sessionVerId: 1);
        sm.OnNegotiate(in neg);

        var est = MakeEstablish(sessionId: 100, sessionVerId: 999); // wrong verId
        var action = sm.OnEstablish(in est);
        Assert.Equal(HandshakeActionKind.Terminate, action.Kind);
        Assert.Equal(TerminationCode.INVALID_SESSIONVERID, action.TermCode);
    }

    [Fact]
    public void ApplicationMessageBeforeEstablished_ReturnsUnnegotiated()
    {
        var sm = new FixpHandshakeStateMachine();
        var action = sm.OnApplicationMessageBeforeEstablished();
        Assert.Equal(HandshakeActionKind.Terminate, action.Kind);
        Assert.Equal(TerminationCode.UNNEGOTIATED, action.TermCode);
    }

    [Fact]
    public void SessionIdAndVerIdStoredAfterNegotiate()
    {
        var sm = new FixpHandshakeStateMachine();
        var neg = MakeNegotiate(sessionId: 77, sessionVerId: 88);
        sm.OnNegotiate(in neg);

        Assert.Equal((uint)77, (uint)sm.SessionId);
        Assert.Equal((ulong)88, (ulong)sm.SessionVerId);
    }
}
