using B3.EntryPoint.Client.Fixp;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Projects an SDK <see cref="FixpClientState"/> into one-hot
/// <c>(state-name, 0|1)</c> rows suitable for an OpenTelemetry observable
/// gauge tagged by state. Callers iterate the result and emit one
/// <c>Measurement</c> per row, with the <c>firm</c> tag added on top.
/// </summary>
/// <remarks>
/// <para>
/// Always emits one row per known <see cref="FixpClientState"/> value so
/// dashboards see a stable cardinality and can graph "is the firm in
/// state X right now?" cleanly. Exactly one row per call has value 1,
/// the rest are 0.
/// </para>
/// <para>
/// State names are emitted in lower-case-with-underscores form
/// (e.g. <c>tcp_connected</c>) to match the convention used by other
/// metric tags in this project.
/// </para>
/// </summary>
public static class FixpStateGaugeProjector
{
    private static readonly (FixpClientState State, string Tag)[] _states =
    {
        (FixpClientState.Disconnected, "disconnected"),
        (FixpClientState.TcpConnected, "tcp_connected"),
        (FixpClientState.Negotiating,  "negotiating"),
        (FixpClientState.Negotiated,   "negotiated"),
        (FixpClientState.Establishing, "establishing"),
        (FixpClientState.Established,  "established"),
        (FixpClientState.Suspended,    "suspended"),
        (FixpClientState.Terminating,  "terminating"),
        (FixpClientState.Terminated,   "terminated"),
    };

    public static IEnumerable<KeyValuePair<string, int>> Project(FixpClientState current)
    {
        foreach (var (state, tag) in _states)
            yield return new KeyValuePair<string, int>(tag, state == current ? 1 : 0);
    }
}
