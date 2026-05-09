namespace B3.Trading.EntryPointListener.Handshake;

/// <summary>
/// Add-ons applied by <c>FixpSessionConnection</c> to
/// <see cref="FixpHandshakeStateMachine"/> so that asynchronous
/// auth + session-registry checks can layer on top of the synchronous
/// FIXP-shape state machine without forking the FSM. Sub-issue #170.
/// </summary>
internal static class FixpHandshakeStateMachineExtensions
{
    /// <summary>
    /// Forces the state machine into <see cref="FixpSessionState.Terminated"/>.
    /// Used after the connection layer overrides an FSM-approved Establish
    /// with a registry-level reject (single-active violation, stale
    /// SessionVerId) so the next inbound frame is short-circuited rather
    /// than being interpreted on a stale state.
    /// </summary>
    public static void ForceTerminated(this FixpHandshakeStateMachine sm)
        => sm.SetState(FixpSessionState.Terminated);
}
