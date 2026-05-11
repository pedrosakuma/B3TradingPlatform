using System.Threading;
using System.Threading.Tasks;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;

namespace B3.Trading.Application.Tests.UserBots;

/// <summary>
/// Unit tests for <see cref="InMemoryUserBotCredentialRegistry"/>
/// (sub-issue #169). Covers PAT shape, bcrypt persistence, list scoping,
/// soft-revoke semantics, malformed-token rejection, and
/// snapshot/WAL-replay reconstruction.
/// </summary>
public class InMemoryUserBotCredentialRegistryTests
{
    private static InMemoryUserBotCredentialRegistry Reg() => new();

    [Fact]
    public async Task Create_ReturnsPlainTokenInExpectedShape()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "morning bot", default);

        Assert.StartsWith("b3t_", c.PlainToken);
        Assert.Matches(@"^b3t_[A-Za-z0-9_\-]{10}_[A-Za-z0-9_\-]{32}$", c.PlainToken);
        Assert.Equal(c.Credential.CredShortId, c.PlainToken.Substring(4, 10));
        Assert.Equal("morning bot", c.Credential.Label);
        Assert.Equal("alice", c.Credential.UserId);
        Assert.Null(c.Credential.RevokedAtUtc);
    }

    [Fact]
    public async Task Create_DoesNotPersistPlaintextSecret()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);

        // Bcrypt hash on the credential — not the secret itself.
        Assert.StartsWith("$2", c.Credential.SecretHash);
        var split = c.PlainToken.Split('_', 3);
        Assert.Equal(3, split.Length);
        var plainSecret = split[2];
        Assert.DoesNotContain(plainSecret, c.Credential.SecretHash);
    }

    [Fact]
    public async Task ListByUser_OnlyReturnsCallersCredentials()
    {
        var r = Reg();
        var a1 = await r.CreateAsync("alice", "a1", default);
        await Task.Delay(2); // keep ordering stable across timestamps
        var a2 = await r.CreateAsync("alice", "a2", default);
        var b1 = await r.CreateAsync("bob", "b1", default);

        var alice = r.ListByUser("alice");
        Assert.Equal(2, alice.Count);
        Assert.All(alice, c => Assert.Equal("alice", c.UserId));
        Assert.Contains(alice, c => c.Id == a1.Credential.Id);
        Assert.Contains(alice, c => c.Id == a2.Credential.Id);

        var bob = r.ListByUser("bob");
        Assert.Single(bob, c => c.Id == b1.Credential.Id);
    }

    [Fact]
    public async Task ListByUser_IncludesRevokedRows()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);
        Assert.True(await r.RevokeAsync("alice", c.Credential.Id, default));

        var rows = r.ListByUser("alice");
        var row = Assert.Single(rows);
        Assert.NotNull(row.RevokedAtUtc);
    }

    [Fact]
    public async Task Revoke_OtherUsersCredential_ReturnsFalseAndDoesNotMutate()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);

        Assert.False(await r.RevokeAsync("bob", c.Credential.Id, default));

        var alice = Assert.Single(r.ListByUser("alice"));
        Assert.Null(alice.RevokedAtUtc);
    }

    [Fact]
    public async Task Revoke_UnknownId_ReturnsFalse()
    {
        var r = Reg();
        Assert.False(await r.RevokeAsync("alice", Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Revoke_TwiceReturnsFalseSecondTime()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);
        Assert.True(await r.RevokeAsync("alice", c.Credential.Id, default));
        Assert.False(await r.RevokeAsync("alice", c.Credential.Id, default));
    }

    [Fact]
    public async Task TryAuthenticate_ResolvesValidToken()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);

        var auth = await r.TryAuthenticateAsync(c.PlainToken, default);
        Assert.NotNull(auth);
        Assert.Equal(c.Credential.Id, auth!.Id);
    }

    [Fact]
    public async Task TryAuthenticate_RejectsRevokedCredential()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);
        await r.RevokeAsync("alice", c.Credential.Id, default);

        Assert.Null(await r.TryAuthenticateAsync(c.PlainToken, default));
    }

    [Fact]
    public async Task TryAuthenticate_RejectsWrongSecretForKnownShortId()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);
        var split = c.PlainToken.Split('_', 3);
        var bad = $"{split[0]}_{split[1]}_{new string('A', split[2].Length)}";

        Assert.Null(await r.TryAuthenticateAsync(bad, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("b3t_short")]
    [InlineData("b3t_aaaaaaaaaa")] // 10 char shortId but no '_' separator + secret
    [InlineData("b3t_aaaaaaaaaaXsecret")] // wrong separator at position 10
    public async Task TryAuthenticate_RejectsMalformedTokens(string token)
    {
        var r = Reg();
        await r.CreateAsync("alice", "x", default); // populate registry

        Assert.Null(await r.TryAuthenticateAsync(token, default));
    }

    [Fact]
    public async Task TryAuthenticate_RejectsUnknownShortId()
    {
        var r = Reg();
        await r.CreateAsync("alice", "x", default);

        var unknown = "b3t_AAAAAAAAAA_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        Assert.Null(await r.TryAuthenticateAsync(unknown, default));
    }

    [Fact]
    public void TryParseToken_RoundTripsSecretContainingUnderscore()
    {
        // Secret half is base64url and may contain '_'. Parser must
        // not split on first '_' (regression: original IndexOf split
        // truncated such secrets).
        var token = "b3t_ABCDEFGHIJ_secret_with_underscores_xyz";
        Assert.True(InMemoryUserBotCredentialRegistry.TryParseToken(token, out var sid, out var sec));
        Assert.Equal("ABCDEFGHIJ", sid);
        Assert.Equal("secret_with_underscores_xyz", sec);
    }

    [Fact]
    public async Task SnapshotRoundTrip_PreservesAllRows()
    {
        var src = Reg();
        var c1 = await src.CreateAsync("alice", "a1", default);
        var c2 = await src.CreateAsync("alice", "a2", default);
        var c3 = await src.CreateAsync("bob", "b1", default);
        await src.RevokeAsync("alice", c1.Credential.Id, default);

        var snap = src.Snapshot();
        Assert.Equal(3, snap.Count);

        var dst = Reg();
        dst.Restore(snap);

        Assert.Equal(2, dst.ListByUser("alice").Count);
        Assert.Single(dst.ListByUser("bob"));

        // Auth still works post-restore for the un-revoked row.
        Assert.NotNull(await dst.TryAuthenticateAsync(c2.PlainToken, default));
        Assert.Null(await dst.TryAuthenticateAsync(c1.PlainToken, default));
        Assert.NotNull(await dst.TryAuthenticateAsync(c3.PlainToken, default));
    }

    [Fact]
    public async Task EventReplay_ReconstructsRegistryFromWal()
    {
        // Mint via a registry wired to a real EventDispatcher so the
        // WAL is populated, then replay those events into a brand-new
        // registry via the internal Apply hooks. The reconstructed
        // state must authenticate the original tokens.
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var producer = new InMemoryUserBotCredentialRegistry(dispatcher);

        var c1 = await producer.CreateAsync("alice", "a1", default);
        var c2 = await producer.CreateAsync("bob", "b1", default);
        await producer.RevokeAsync("alice", c1.Credential.Id, default);

        var consumer = new InMemoryUserBotCredentialRegistry();
        foreach (var e in store.Events)
        {
            switch (e)
            {
                case UserBotCredentialCreatedEvent created:
                    consumer.ApplyCreated(new UserBotCredential(
                        created.Id, created.UserId, created.CredShortId, created.Label,
                        created.SecretHash, created.CreatedAtUtc, RevokedAtUtc: null));
                    break;
                case UserBotCredentialRevokedEvent revoked:
                    consumer.ApplyRevoked(revoked.Id, revoked.RevokedAtUtc);
                    break;
            }
        }

        var aliceRows = consumer.ListByUser("alice");
        Assert.Single(aliceRows);
        Assert.NotNull(aliceRows[0].RevokedAtUtc);

        Assert.Null(await consumer.TryAuthenticateAsync(c1.PlainToken, default));
        Assert.NotNull(await consumer.TryAuthenticateAsync(c2.PlainToken, default));
    }

    private sealed class RecordingEventStore : IEventStore
    {
        public List<WalEvent> Events { get; } = new();
        public long CurrentSeq { get; private set; }
        public long Append(WalEvent evt)
        {
            Events.Add(evt);
            return ++CurrentSeq;
        }
        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) => Append(evt);
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            for (var i = 0; i < Events.Count; i++)
            {
                var seq = i + 1;
                if (seq > sinceSeqExclusive) yield return (seq, Events[i]);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
