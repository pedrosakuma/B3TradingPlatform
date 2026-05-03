using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Minimal <see cref="IOptionsMonitor{T}"/> for unit tests that need
/// to drive hot-reload behavior without spinning up the full
/// configuration system. Subscribers registered via
/// <see cref="OnChange"/> are invoked synchronously when
/// <see cref="Set(T)"/> is called.
/// </summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T> where T : class
{
    private T _current;
    private readonly List<Action<T, string?>> _listeners = new();

    public StaticOptionsMonitor(T initial) => _current = initial;

    public T CurrentValue => _current;

    public T Get(string? name) => _current;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(() => _listeners.Remove(listener));
    }

    public void Set(T value)
    {
        _current = value;
        foreach (var l in _listeners.ToArray()) l(value, null);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        public Subscription(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}
