using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using B3.Trading.Application.Outbound;

namespace B3.Trading.Application.Tests.Outbound;

public sealed class RestOrderIdempotencyStoreTests
{
    [Fact]
    public async Task SnapshotRestore_PreservesReplayAndConflictSemantics()
    {
        var protector = CreateProtector();
        var store = new RestOrderIdempotencyStore(protector);
        var identity = new RestOrderIdempotencyIdentity(
            "FIRM-A",
            "alice",
            "alice",
            "POST /orders",
            "restart-key");
        var hash = Hash("request-a");
        var created = await store.ExecuteAsync(
            identity,
            hash,
            context =>
            {
                store.Apply(context.Binding with { ClOrdId = 1234 });
                return Task.FromResult(1234UL);
            });

        var restored = new RestOrderIdempotencyStore(protector);
        restored.Restore(store.CaptureSnapshot());
        var replayed = await restored.ExecuteAsync<ulong>(
            identity,
            hash,
            _ => throw new InvalidOperationException("must not create"));
        var conflict = await restored.ExecuteAsync<ulong>(
            identity,
            Hash("request-b"),
            _ => throw new InvalidOperationException("must not create"));

        Assert.Equal(RestOrderIdempotencyExecutionKind.Created, created.Kind);
        Assert.Equal(RestOrderIdempotencyExecutionKind.Replayed, replayed.Kind);
        Assert.Equal(RestOrderIdempotencyExecutionKind.Conflict, conflict.Kind);
        Assert.Equal(created.Binding, replayed.Binding);
    }

    [Fact]
    public async Task SnapshotRestore_PreservesMultipleKeysBoundToOneMutation()
    {
        var protector = CreateProtector();
        var store = new RestOrderIdempotencyStore(protector);
        var first = await store.ExecuteAsync(
            Identity("first-key"),
            Hash("request"),
            context =>
            {
                store.Apply(context.Binding with { ClOrdId = 1234 });
                return Task.FromResult(0);
            });
        var alias = await store.ExecuteAsync(
            Identity("alias-key"),
            Hash("request"),
            context =>
            {
                store.Apply(context.Binding with
                {
                    MutationId = first.Binding!.MutationId,
                    ClOrdId = first.Binding.ClOrdId,
                });
                return Task.FromResult(0);
            });
        var restored = new RestOrderIdempotencyStore(protector);
        restored.Restore(store.CaptureSnapshot());

        var replayed = await restored.ExecuteAsync<int>(
            Identity("alias-key"),
            Hash("request"),
            _ => throw new InvalidOperationException("alias must replay"));

        Assert.Equal(RestOrderIdempotencyExecutionKind.Created, alias.Kind);
        Assert.Equal(RestOrderIdempotencyExecutionKind.Replayed, replayed.Kind);
        Assert.Equal(first.Binding!.MutationId, replayed.Binding!.MutationId);
        Assert.Equal(2, restored.CaptureSnapshot().Count);
    }

    [Fact]
    public async Task DurableRecord_ContainsNoPlaintextKeyOrOwner()
    {
        var protector = CreateProtector();
        var store = new RestOrderIdempotencyStore(protector);
        const string key = "plain-secret-key";
        const string owner = "plain-owner";

        await store.ExecuteAsync(
            new RestOrderIdempotencyIdentity(
                "FIRM-A",
                owner,
                owner,
                "POST /orders",
                key),
            Hash("request"),
            context =>
            {
                store.Apply(context.Binding with { ClOrdId = 1234 });
                return Task.FromResult(0);
            });
        var json = JsonSerializer.Serialize(store.CaptureSnapshot());

        Assert.DoesNotContain(key, json, StringComparison.Ordinal);
        Assert.DoesNotContain(owner, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotationWithHistoricalStableKey_ReplaysInsteadOfCreatingDuplicate()
    {
        var oldProtector = CreateProtector("old", ["old"]);
        var original = new RestOrderIdempotencyStore(oldProtector);
        var identity = Identity("rotation-key");
        var hash = Hash("request");
        var created = await original.ExecuteAsync(
            identity,
            hash,
            context =>
            {
                original.Apply(context.Binding with { ClOrdId = 1234 });
                return Task.FromResult(1234UL);
            });
        var rotated = new RestOrderIdempotencyStore(
            CreateProtector("new", ["old", "new"]));
        rotated.Restore(original.CaptureSnapshot());

        var replayed = await rotated.ExecuteAsync<ulong>(
            identity,
            hash,
            _ => throw new InvalidOperationException("rotation must not create"));

        Assert.Equal(RestOrderIdempotencyExecutionKind.Replayed, replayed.Kind);
        Assert.Equal(created.Binding, replayed.Binding);
        Assert.True(rotated.IsOwnedBy(
            replayed.Binding!,
            "FIRM-A",
            "alice",
            "alice",
            "POST /orders"));
    }

    [Fact]
    public async Task MissingHistoricalStableKey_FailsClosedForSameAndNewKeys()
    {
        var original = new RestOrderIdempotencyStore(
            CreateProtector("old", ["old"]));
        await original.ExecuteAsync(
            Identity("old-key"),
            Hash("request"),
            context =>
            {
                original.Apply(context.Binding with { ClOrdId = 1234 });
                return Task.FromResult(0);
            });
        var rotated = new RestOrderIdempotencyStore(
            CreateProtector("new", ["new"]));
        rotated.Restore(original.CaptureSnapshot());

        await Assert.ThrowsAsync<RestOrderIdempotencyUnavailableException>(
            () => rotated.ExecuteAsync<int>(
                Identity("old-key"),
                Hash("request"),
                _ => Task.FromResult(1)));
        await Assert.ThrowsAsync<RestOrderIdempotencyUnavailableException>(
            () => rotated.ExecuteAsync<int>(
                Identity("different-key"),
                Hash("different-request"),
                _ => Task.FromResult(1)));
        Assert.Single(rotated.CaptureSnapshot());
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static RestOrderIdempotencyIdentity Identity(string key) =>
        new("FIRM-A", "alice", "alice", "POST /orders", key);

    private static AeadOutboundCommandProtector CreateProtector() =>
        CreateProtector("test", ["test"]);

    private static AeadOutboundCommandProtector CreateProtector(
        string activeStableKeyId,
        string[] keyIds) =>
        new(
            new OutboundCommandProtectionOptions
            {
                ActiveKeyId = activeStableKeyId,
                ActiveKeyVersion = 1,
                StableReferenceKeyId = activeStableKeyId,
                StableReferenceKeyVersion = 1,
                Keys = keyIds.Select(id =>
                    new OutboundCommandProtectionKeyOptions
                    {
                        KeyId = id,
                        Version = 1,
                        KeyBase64 = Convert.ToBase64String(
                            SHA256.HashData(Encoding.UTF8.GetBytes(
                                $"rest-order-idempotency-store-tests:{id}"))),
                    }).ToList(),
            });
}
