using System.ComponentModel.DataAnnotations;

namespace B3.Trading.SampleBot;

public sealed class SampleBotOptions
{
    public const string SectionName = "SampleBot";

    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan InitialSnapshotTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public string? SubAccountId { get; set; }

    public SampleBotAuthOptions Auth { get; set; } = new();

    public DemoOrderOptions DemoOrder { get; set; } = new();
}

public sealed class SampleBotAuthOptions
{
    public SampleBotAuthMode Mode { get; set; } = SampleBotAuthMode.InternalToken;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? ExternalAccessToken { get; set; }

    public string? InternalTradingToken { get; set; }
}

public enum SampleBotAuthMode
{
    LocalPassword,
    ExternalExchange,
    InternalToken,
}

public sealed class DemoOrderOptions
{
    public bool Enabled { get; set; }

    public string Symbol { get; set; } = "PETR4";

    public ulong SecurityId { get; set; }

    public string Side { get; set; } = "Buy";

    [Range(1, long.MaxValue)]
    public long Quantity { get; set; } = 100;

    [Range(typeof(decimal), "0.01", "1000000")]
    public decimal Price { get; set; } = 30m;

    public bool AutoCancel { get; set; } = true;

    public TimeSpan CancelDelay { get; set; } = TimeSpan.FromSeconds(2);

    public bool ExitAfterWorkflow { get; set; } = true;

    public TimeSpan PostWorkflowWait { get; set; } = TimeSpan.FromSeconds(3);

    public string IdempotencyKeyPrefix { get; set; } = "samplebot";
}
