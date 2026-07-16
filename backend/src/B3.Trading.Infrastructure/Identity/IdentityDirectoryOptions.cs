namespace B3.Trading.Infrastructure.Identity;

public sealed class IdentityDirectoryOptions
{
    public const string SectionName = "Trading:IdentityDirectory";

    public string Provider { get; set; } = IdentityDirectoryProviders.InMemory;
    public string? Path { get; set; }
    public bool MigrateOnStartup { get; set; } = true;
    public bool ImportLegacyUsersOnStartup { get; set; } = true;
    public int BusyTimeoutMilliseconds { get; set; } = 5_000;
    public int ExpectedWriterCount { get; set; } = 1;
}

public static class IdentityDirectoryProviders
{
    public const string InMemory = "InMemory";
    public const string Sqlite = "Sqlite";
}
