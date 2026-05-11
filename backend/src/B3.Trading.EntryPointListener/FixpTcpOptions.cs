namespace B3.Trading.EntryPointListener;

/// <summary>
/// TCP-level socket tunables applied to every <see cref="System.Net.Sockets.TcpClient"/>
/// accepted by <see cref="Hosting.FixpListenerHostedService"/> (RFC §5.9 / P11).
///
/// <para>Defaults disable Nagle (<see cref="NoDelay"/> = true) and pin
/// 64 KiB send/receive buffers. Default OS buffers vary across platforms
/// (Linux auto-tunes, Windows defaults to 8 KiB which under-serves the
/// SOFH frame burst rate) — setting them explicitly makes the listener
/// behave predictably across deployments.</para>
/// </summary>
public sealed class FixpTcpOptions
{
    /// <summary>
    /// Per-connection send buffer (SO_SNDBUF) in bytes. Default 64 KiB.
    /// </summary>
    public int SendBufferBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Per-connection receive buffer (SO_RCVBUF) in bytes. Default 64 KiB.
    /// </summary>
    public int ReceiveBufferBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Disable Nagle's algorithm (TCP_NODELAY). Default <c>true</c> — the
    /// FIXP/OUCH delivery path must not batch small writes up to 200 ms.
    /// </summary>
    public bool NoDelay { get; set; } = true;
}
