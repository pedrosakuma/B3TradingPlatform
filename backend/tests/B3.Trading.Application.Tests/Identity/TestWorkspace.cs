namespace B3.Trading.Application.Tests.Identity;

public sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string path) => Path = path;

    public string Path { get; }

    public static TestWorkspace Create(string name)
    {
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var root = System.IO.Path.Combine(
            Directory.GetCurrentDirectory(),
            "TestResults",
            "Identity",
            safe + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TestWorkspace(root);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
