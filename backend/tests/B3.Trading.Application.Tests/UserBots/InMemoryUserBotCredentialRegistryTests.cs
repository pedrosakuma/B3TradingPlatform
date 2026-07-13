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
    public void BCrypt_LegacyHashFromV4_0_3_StillVerifiesUnderCurrentVersion()
    {
        // Hash produced by BCrypt.Net-Next 4.0.3 (the previous pinned version)
        // for the plaintext "hunter2-legacy" with workFactor=11. Embedded as a
        // literal so the test fails loud if a future BCrypt bump ever breaks
        // the on-disk hash format used by stored bot credentials.
        const string legacyHash = "$2a$11$uSDHF7qiiPXH6ieX8MD8SOzSdu00/4PBrjkJcy/UulOLvvFPt1sj2";
        Assert.True(BCrypt.Net.BCrypt.Verify("hunter2-legacy", legacyHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("wrong-password", legacyHash));
    }

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

    // ───────────────────────── #431 firm attribution ─────────────────────────

    [Fact]
    public async Task CreateAsync_DefaultFirmId_IsLegacyDefaultSentinel()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);
        // Pre-#431 callers (and the omitted-argument case) must keep
        // attributing to the legacy "default" sentinel — that is what
        // the listener has always emitted and what PositionKeeper
        // bookkeeping expects when only one firm is configured.
        Assert.Equal("default", c.Credential.FirmId);
    }

    [Fact]
    public async Task CreateAsync_ExplicitFirmId_PropagatesIntoCredentialAndEvent()
    {
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var r = new InMemoryUserBotCredentialRegistry(dispatcher);

        var c = await r.CreateAsync("alice", "x", default, firmId: "alpha");
        Assert.Equal("alpha", c.Credential.FirmId);

        var created = Assert.IsType<UserBotCredentialCreatedEvent>(Assert.Single(store.Events));
        Assert.Equal("alpha", created.FirmId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task CreateAsync_BlankFirmId_Rejected(string? firmId)
    {
        var r = Reg();
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            r.CreateAsync("alice", "x", default, firmId: firmId!));
    }

    [Fact]
    public async Task SnapshotRoundTrip_PreservesFirmId_AndHydratesLegacyAsDefault()
    {
        var src = Reg();
        var alpha = await src.CreateAsync("alice", "a", default, firmId: "alpha");
        var beta = await src.CreateAsync("bob", "b", default, firmId: "beta");

        var snap = src.Snapshot().ToList();
        Assert.All(snap, row => Assert.NotNull(row.FirmId));

        // Splice in a legacy snapshot row (FirmId=null) and ensure the
        // restored credential hydrates as the legacy "default" sentinel
        // — the back-compat invariant for pre-#431 builds.
        snap.Add(new UserBotCredentialSnapshot(
            Id: Guid.NewGuid(),
            UserId: "carol",
            CredShortId: "LEGACY1234",
            Label: "legacy",
            SecretHash: "$2a$12$" + new string('a', 53),
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RevokedAtUtc: null,
            FirmId: null));

        var dst = Reg();
        dst.Restore(snap);

        Assert.Equal("alpha", Assert.Single(dst.ListByUser("alice")).FirmId);
        Assert.Equal("beta", Assert.Single(dst.ListByUser("bob")).FirmId);
        Assert.Equal("default", Assert.Single(dst.ListByUser("carol")).FirmId);
    }

    [Fact]
    public async Task EventReplay_PreservesExplicitFirmId_AndHydratesLegacyNullAsDefault()
    {
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var producer = new InMemoryUserBotCredentialRegistry(dispatcher);

        var modern = await producer.CreateAsync("alice", "a", default, firmId: "alpha");

        // Synthesize a legacy WAL event whose FirmId field was never set
        // (the persisted JSON wouldn't carry the property at all).
        var legacy = new UserBotCredentialCreatedEvent
        {
            Id = Guid.NewGuid(),
            UserId = "bob",
            CredShortId = "LEGACY9999",
            Label = "legacy",
            SecretHash = modern.Credential.SecretHash,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            FirmId = null,
        };

        var consumer = new InMemoryUserBotCredentialRegistry();
        foreach (var e in store.Events.Concat(new WalEvent[] { legacy }))
        {
            if (e is UserBotCredentialCreatedEvent c)
            {
                consumer.ApplyCreated(new UserBotCredential(
                    c.Id, c.UserId, c.CredShortId, c.Label, c.SecretHash,
                    c.CreatedAtUtc, RevokedAtUtc: null,
                    FirmId: string.IsNullOrEmpty(c.FirmId) ? "default" : c.FirmId));
            }
        }

        Assert.Equal("alpha", Assert.Single(consumer.ListByUser("alice")).FirmId);
        Assert.Equal("default", Assert.Single(consumer.ListByUser("bob")).FirmId);
    }

    [Fact]
    public async Task TryAuthenticate_RestoresFirmIdOnReturnedCredential()
    {
        // Sanity: the listener fishes BotSessionPrincipal.FirmId out of
        // the row returned by TryAuthenticateAsync — that row must carry
        // the firmId chosen at creation time, not the "default" sentinel.
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default, firmId: "alpha");

        var resolved = await r.TryAuthenticateAsync(c.PlainToken, default);
        Assert.NotNull(resolved);
        Assert.Equal("alpha", resolved!.FirmId);
    }

    // ─── Cert↔credential binding (sub-issue #540) ────────────────────────────

    private const string SampleThumbprint =
        "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public async Task Create_WithPin_NormalizesAndStoresThumbprint()
    {
        var r = Reg();
        // Lower-case + colon-separated input must canonicalize to upper-case 64-hex.
        var pinned = await r.CreateAsync("alice", "pinned",
            "ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89",
            default);

        Assert.Equal(SampleThumbprint, pinned.Credential.BoundCertThumbprint);
    }

    [Fact]
    public async Task Create_Unpinned_HasNullThumbprint()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);
        Assert.Null(c.Credential.BoundCertThumbprint);

        var c2 = await r.CreateAsync("alice", "y", "   ", default);
        Assert.Null(c2.Credential.BoundCertThumbprint);
    }

    [Theory]
    [InlineData("deadbeef")]                                                   // too short
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF012345678")] // 63
    [InlineData("ZZCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")] // non-hex
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF01234567890")] // 65, too long
    public async Task Create_WithMalformedPin_Throws(string bad)
    {
        var r = Reg();
        await Assert.ThrowsAsync<ArgumentException>(() => r.CreateAsync("alice", "x", bad, default));
    }

    [Fact]
    public async Task Create_WithOversizedPin_ThrowsWithoutStackOverflow()
    {
        // Guards against sizing a stackalloc buffer from untrusted input length.
        var r = Reg();
        var huge = new string('A', 10_000_000);
        await Assert.ThrowsAsync<ArgumentException>(() => r.CreateAsync("alice", "x", huge, default));
    }

    [Fact]
    public async Task SetBoundCertThumbprint_SetsRepinsAndClears()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);

        Assert.True(await r.SetBoundCertThumbprintAsync("alice", c.Credential.Id, SampleThumbprint, default));
        Assert.Equal(SampleThumbprint, r.ListByUser("alice").Single().BoundCertThumbprint);

        var other = "0000000000000000000000000000000000000000000000000000000000000000";
        Assert.True(await r.SetBoundCertThumbprintAsync("alice", c.Credential.Id, other, default));
        Assert.Equal(other, r.ListByUser("alice").Single().BoundCertThumbprint);

        Assert.True(await r.SetBoundCertThumbprintAsync("alice", c.Credential.Id, null, default));
        Assert.Null(r.ListByUser("alice").Single().BoundCertThumbprint);
    }

    [Fact]
    public async Task SetBoundCertThumbprint_CrossUserOrMissingOrRevoked_ReturnsFalse()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);

        // Wrong user → indistinguishable from missing.
        Assert.False(await r.SetBoundCertThumbprintAsync("bob", c.Credential.Id, SampleThumbprint, default));
        // Unknown id.
        Assert.False(await r.SetBoundCertThumbprintAsync("alice", Guid.NewGuid(), SampleThumbprint, default));

        await r.RevokeAsync("alice", c.Credential.Id, default);
        Assert.False(await r.SetBoundCertThumbprintAsync("alice", c.Credential.Id, SampleThumbprint, default));
    }

    [Fact]
    public async Task SetBoundCertThumbprint_MalformedPin_Throws()
    {
        var r = Reg();
        var c = await r.CreateAsync("alice", "x", default);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            r.SetBoundCertThumbprintAsync("alice", c.Credential.Id, "not-a-thumbprint", default));
    }

    [Fact]
    public async Task SnapshotRoundTrip_PreservesBoundCertThumbprint()
    {
        var src = Reg();
        var pinned = await src.CreateAsync("alice", "pinned", SampleThumbprint, default);
        var unpinned = await src.CreateAsync("alice", "unpinned", default);

        var dst = Reg();
        dst.Restore(src.Snapshot());

        var rows = dst.ListByUser("alice");
        Assert.Equal(SampleThumbprint, rows.Single(c => c.Id == pinned.Credential.Id).BoundCertThumbprint);
        Assert.Null(rows.Single(c => c.Id == unpinned.Credential.Id).BoundCertThumbprint);
    }

    [Fact]
    public async Task EventReplay_ReconstructsBoundCertThumbprint()
    {
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var producer = new InMemoryUserBotCredentialRegistry(dispatcher);

        var c = await producer.CreateAsync("alice", "pinned", SampleThumbprint, default);
        var other = "1111111111111111111111111111111111111111111111111111111111111111";
        await producer.SetBoundCertThumbprintAsync("alice", c.Credential.Id, other, default);

        var consumer = new InMemoryUserBotCredentialRegistry();
        foreach (var e in store.Events)
        {
            switch (e)
            {
                case UserBotCredentialCreatedEvent created:
                    consumer.ApplyCreated(new UserBotCredential(
                        created.Id, created.UserId, created.CredShortId, created.Label,
                        created.SecretHash, created.CreatedAtUtc, RevokedAtUtc: null,
                        BoundCertThumbprint: created.BoundCertThumbprint));
                    break;
                case UserBotCredentialCertBindingChangedEvent changed:
                    consumer.ApplyCertBindingChanged(changed.Id, changed.BoundCertThumbprint);
                    break;
            }
        }

        Assert.Equal(other, consumer.ListByUser("alice").Single().BoundCertThumbprint);
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
