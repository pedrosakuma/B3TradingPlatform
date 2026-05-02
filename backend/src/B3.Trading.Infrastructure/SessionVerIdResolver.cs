namespace B3.Trading.Infrastructure;

/// <summary>
/// Computes the next FIXP <c>SessionVerId</c> to advertise on
/// <c>EntryPointClientOptions</c> at gateway construction.
///
/// <para>
/// The B3 gateway rejects a Negotiate whose <c>SessionVerId</c> is not
/// strictly greater than any previously seen value for the same
/// <c>SessionId</c> (terminates with <c>InvalidSessionVerId</c>). After
/// a process crash, the SDK's <see cref="B3.EntryPoint.Client.State.FileSessionStateStore"/>
/// holds the last persisted version; we bump from that value rather
/// than re-reading the (likely stale) configured value, otherwise the
/// host can wedge in a permanent reconnect-loop after restart.
/// </para>
///
/// <para>
/// Returned value is <c>max(configured, persisted + 1)</c>: the
/// configured value still wins when it has been bumped externally
/// (operator override) but the persisted floor is always honoured.
/// </para>
/// </summary>
public static class SessionVerIdResolver
{
    public static uint Resolve(uint configured, uint? persisted)
    {
        if (persisted is null) return configured;
        var floor = checked(persisted.Value + 1);
        return configured > floor ? configured : floor;
    }
}
