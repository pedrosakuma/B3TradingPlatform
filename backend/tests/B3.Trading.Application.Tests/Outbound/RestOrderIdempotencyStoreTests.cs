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

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static AeadOutboundCommandProtector CreateProtector() =>
        new(
            new OutboundCommandProtectionOptions
            {
                ActiveKeyId = "test",
                ActiveKeyVersion = 1,
                Keys =
                [
                    new OutboundCommandProtectionKeyOptions
                    {
                        KeyId = "test",
                        Version = 1,
                        KeyBase64 = Convert.ToBase64String(
                            SHA256.HashData(Encoding.UTF8.GetBytes(
                                "rest-order-idempotency-store-tests"))),
                    },
                ],
            });
}
