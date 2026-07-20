using B3.Trading.Application.Outbound;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.Persistence;

public sealed class ActiveHostFenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Directory.GetCurrentDirectory(),
        ".test-artifacts",
        "active-host-fence",
        Guid.NewGuid().ToString("N"));

    public ActiveHostFenceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SecondHostLoses_AndNextExclusiveAcquisitionAdvancesDurableEpoch()
    {
        using var first = CreateFence(out var firstEpoch, out var firstRecovery);
        Assert.True(first.TryAcquire());
        Assert.True(first.IsHeld);
        Assert.Equal(1, firstEpoch.Sequence);
        Assert.Equal(OutboundRecoveryPhase.WaitingForFence, firstRecovery.Phase);

        using var loser = CreateFence(out var loserEpoch, out var loserRecovery);
        Assert.False(loser.TryAcquire());
        Assert.False(loser.IsHeld);
        Assert.False(loserEpoch.IsInitialized);
        Assert.Equal(OutboundRecoveryPhase.FenceUnavailable, loserRecovery.Phase);

        first.Dispose();
        using var next = CreateFence(out var nextEpoch, out _);
        Assert.True(next.TryAcquire());
        Assert.Equal(2, nextEpoch.Sequence);
        Assert.NotEqual(firstEpoch.Id, nextEpoch.Id);
    }

    [Fact]
    public void CorruptEpochFailsClosed()
    {
        using (var first = CreateFence(out _, out _))
            Assert.True(first.TryAcquire());

        File.WriteAllText(
            Path.Combine(_root, "deployment", "process-epoch.json"),
            "{not-json");
        using var next = CreateFence(out var epoch, out var recovery);

        Assert.False(next.TryAcquire());
        Assert.False(epoch.IsInitialized);
        Assert.Equal(OutboundRecoveryPhase.FenceUnavailable, recovery.Phase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ActiveHostFence CreateFence(
        out OutboundProcessEpoch epoch,
        out OutboundRecoveryState recovery)
    {
        epoch = OutboundProcessEpoch.CreateUninitialized();
        recovery = new OutboundRecoveryState(new OutboundMutationLedger());
        return new ActiveHostFence(
            Options.Create(new PersistenceOptions
            {
                Enabled = true,
                DataDirectory = _root,
                FirmId = "deployment",
            }),
            Options.Create(new ExchangeOptions
            {
                Firms =
                [
                    new FirmConfig { FirmId = "FIRM01" },
                ],
            }),
            epoch,
            recovery,
            NullLogger<ActiveHostFence>.Instance);
    }
}
