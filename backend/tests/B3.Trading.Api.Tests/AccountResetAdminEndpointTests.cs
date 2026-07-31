using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Application;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #671/#753 (RFC: admin account reset, PR 3). Coverage for
/// <c>POST /api/admin/accounts/{endClientId}/reset</c>: auth role gate,
/// firm-claim fail-closed (RFC #753: firm scope comes EXCLUSIVELY from
/// the caller's JWT), the fail-closed 409 guard (open working order OR
/// non-terminal/reconciliation-pending outbound mutation — NEVER auto-
/// resolved), happy-path absolute reset (with and without configured
/// seeds), firm isolation, named sub-account bucket clearing, and
/// margin-reservation release. Mirrors
/// <see cref="PositionAdjustmentAdminEndpointTests"/>'s shape for the
/// sibling <c>/positions</c> endpoint.
/// </summary>
public class AccountResetAdminEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AccountResetAdminEndpointTests(TestAppFactory factory) => _factory = factory;

    private static string UniqueClient(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public async Task Post_RequiresAdminRole_TraderGets403()
    {
        using var trader = await _factory.CreateAuthedClientAsync(); // alice (user role)
        var resp = await trader.PostAsync($"/api/admin/accounts/{UniqueClient("anyone")}/reset", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Post_HappyPath_NoSeeds_ZerosCashAndFlattensPositions()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var positions = _factory.Services.GetRequiredService<PositionKeeper>();
        var cashLedger = _factory.Services.GetRequiredService<CashLedger>();
        var endclient = UniqueClient("alice");
        var owner = new EndClientId(endclient);

        // Establish pre-reset state directly, bypassing full HTTP order/
        // fill round-trips (same shortcut PositionAdjustmentAdminEndpointTests
        // relies on for the sibling /positions endpoint).
        var setup = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient,
            symbol = "PETR4",
            netQuantity = 500,
            averageEntryPrice = 28.5m,
        });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        var resp = await admin.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var petr4 = positions.ForEndClientAndFirm(PositionKeeper.DefaultFirmId, owner).Single(p => p.Symbol == "PETR4");
        Assert.Equal(0, petr4.NetQuantity);
        Assert.Equal(0m, petr4.AverageEntryPrice);
        Assert.Equal(0m, cashLedger.GetAvailable(PositionKeeper.DefaultFirmId, owner));
    }

    [Fact]
    public async Task Post_HappyPath_WithConfiguredSeeds_RestoresSeedValues()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Cash:Seeds:0:FirmId"] = "FIRM01",
            ["Trading:Cash:Seeds:0:EndClientId"] = "seeded-user",
            ["Trading:Cash:Seeds:0:InitialAvailable"] = "25000",
            ["Trading:Positions:Seeds:0:EndClientId"] = "seeded-user",
            ["Trading:Positions:Seeds:0:Firm"] = "FIRM01",
            ["Trading:Positions:Seeds:0:Symbol"] = "VALE3",
            ["Trading:Positions:Seeds:0:Quantity"] = "150",
            ["Trading:Positions:Seeds:0:AverageEntryPrice"] = "55",
        });
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var positions = factory.Services.GetRequiredService<PositionKeeper>();
        var cashLedger = factory.Services.GetRequiredService<CashLedger>();
        var owner = new EndClientId("seeded-user");

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync("/api/admin/accounts/seeded-user/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal(25_000m, cashLedger.GetAvailable("FIRM01", owner));
        var vale = Assert.Single(positions.ForEndClientAndFirm("FIRM01", owner));
        Assert.Equal("VALE3", vale.Symbol);
        Assert.Equal(150, vale.NetQuantity);
        Assert.Equal(55m, vale.AverageEntryPrice);
    }

    [Fact]
    public async Task Post_FirmIsolation_OnlyTargetFirmAffected()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var positions = factory.Services.GetRequiredService<PositionKeeper>();
        var owner = new EndClientId("shared-name");

        positions.SetAbsolute("FIRM01", owner, "PETR4", 100, 20m);
        positions.SetAbsolute("FIRM02", owner, "PETR4", 200, 25m);

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync("/api/admin/accounts/shared-name/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var firm1 = positions.ForEndClientAndFirm("FIRM01", owner).Single(p => p.Symbol == "PETR4");
        Assert.Equal(0, firm1.NetQuantity);

        // FIRM02's same-named end-client must be completely untouched.
        var firm2 = positions.ForEndClientAndFirm("FIRM02", owner).Single(p => p.Symbol == "PETR4");
        Assert.Equal(200, firm2.NetQuantity);
        Assert.Equal(25m, firm2.AverageEntryPrice);
    }

    [Fact]
    public async Task Post_BlankFirmClaim_FailsClosed_Returns401()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        using var client = factory.CreateClient();

        var (token, _) = issuer.Issue("admin-op", "admin", firm: "   ");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{UniqueClient("mallory")}/reset", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Post_MissingFirmClaim_FailsClosed_Returns401()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        using var client = factory.CreateClient();

        // Hand-craft a token with NO firm claim at all — mirrors
        // PositionAdjustmentAdminEndpointTests.MissingFirmClaim_FailsClosed_Returns401
        // since JwtIssuer.Issue itself has no way to omit the claim.
        var authOptions = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var keyBytes = Encoding.UTF8.GetBytes(authOptions.SigningKey);
        var credentials = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "admin-op"),
            new(JwtIssuer.RoleClaim, "admin"),
            // Deliberately no JwtIssuer.FirmClaim.
        };
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(
            issuer: authOptions.Issuer,
            audience: authOptions.Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(5),
            signingCredentials: credentials);
        var token = new JwtSecurityTokenHandler().WriteToken(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{UniqueClient("trent")}/reset", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Post_BlankEndClientId_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");

        // A truly empty path segment does not route-match {endClientId}
        // at all (ASP.NET routing 404s before reaching application code) —
        // a URL-encoded whitespace segment DOES bind (as a non-empty,
        // all-whitespace string) and exercises HandleAccountReset's own
        // IsNullOrWhiteSpace guard.
        var resp = await admin.PostAsync("/api/admin/accounts/%20/reset", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_Returns409_WhenOpenWorkingOrderExists_AndDoesNotAutoCancel()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var endclient = "trader-with-open-order";
        var owner = new EndClientId(endclient);

        // Directly insert a working order — defaults to OrderStatus.PendingNew
        // (non-terminal), bypassing the full HTTP submit round-trip
        // (mirrors MultiFirmIsolationTests' seeding pattern).
        var order = new Order(9001UL, owner, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM01");
        Assert.True(book.TryAdd(order));

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // The order must NOT have been auto-cancelled — RFC #753: never
        // auto-cancel/auto-resolve, the operator must clear it manually.
        Assert.Equal(1, book.CountOpenForOwnerAndFirm("FIRM01", owner));
        Assert.True(book.TryGet(9001UL, out var stillThere));
        Assert.NotNull(stillThere);
        Assert.Equal(OrderStatus.PendingNew, stillThere!.Status);
    }

    [Fact]
    public async Task Post_Returns409_WhenNonTerminalOutboundMutationExists()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var commandProtector = factory.Services.GetRequiredService<IOutboundCommandProtector>();
        var outboundLedger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        var endclient = "trader-with-pending-mutation";

        var refCandidates = commandProtector.CreateStableEndClientRefCandidates("FIRM01", endclient);
        var endClientRef = refCandidates.First();

        outboundLedger.Restore(
            new[] { NonTerminalGuardSnapshot("FIRM01", endClientRef, 12345UL, OutboundMutationState.ApprovedToSend) },
            Array.Empty<OutboundCorrelationTombstone>(),
            Array.Empty<InboundVenueEvidenceSnapshot>());

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Post_Returns409_WhenTerminalButRequiresReconciliation()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var commandProtector = factory.Services.GetRequiredService<IOutboundCommandProtector>();
        var outboundLedger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        var endclient = "trader-with-reconciliation-pending";

        var refCandidates = commandProtector.CreateStableEndClientRefCandidates("FIRM01", endclient);
        var endClientRef = refCandidates.First();

        // Terminal (VenueAcknowledged) but RequiresReconciliation=true —
        // the venue outcome is not yet authoritative and must still
        // fail-close a reset (RFC #753).
        outboundLedger.Restore(
            new[] { NonTerminalGuardSnapshot(
                "FIRM01", endClientRef, 12346UL, OutboundMutationState.VenueAcknowledged, requiresReconciliation: true) },
            Array.Empty<OutboundCorrelationTombstone>(),
            Array.Empty<InboundVenueEvidenceSnapshot>());

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Post_TerminalReconciledMutation_DoesNotBlock()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var commandProtector = factory.Services.GetRequiredService<IOutboundCommandProtector>();
        var outboundLedger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        var endclient = "trader-fully-terminal";

        var refCandidates = commandProtector.CreateStableEndClientRefCandidates("FIRM01", endclient);
        var endClientRef = refCandidates.First();

        // Terminal AND RequiresReconciliation=false — the venue outcome
        // is authoritative; nothing should block a reset.
        outboundLedger.Restore(
            new[] { NonTerminalGuardSnapshot(
                "FIRM01", endClientRef, 12347UL, OutboundMutationState.VenueAcknowledged, requiresReconciliation: false) },
            Array.Empty<OutboundCorrelationTombstone>(),
            Array.Empty<InboundVenueEvidenceSnapshot>());

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Post_Returns409_WhenStaleWorkingOrderExists_AndDoesNotAutoCancel()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var endclient = "trader-with-stale-order";
        var owner = new EndClientId(endclient);

        // #671/#753 code-review addendum #1: a STALE order (venue-side
        // disposition no longer positively confirmed) must still block
        // reset — unlike MaxOpenOrdersCheck's risk budget, which
        // deliberately exempts stale orders so a venue-desync ghost
        // doesn't freeze new trading.
        var order = new Order(9101UL, owner, "PETR4", 9101UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM01");
        Assert.True(book.TryAdd(order));
        order.MarkWorking();
        Assert.True(order.MarkStale("inbound_gap:1-2", DateTimeOffset.UtcNow));

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // Never auto-cancel/auto-resolve — the operator must clear the
        // stale order manually.
        Assert.True(book.TryGet(9101UL, out var stillThere));
        Assert.NotNull(stillThere);
        Assert.True(stillThere!.IsStale);
        Assert.Equal(OrderStatus.Working, stillThere.Status);
    }

    [Fact]
    public async Task Post_StaleWorkingOrder_FirmIsolation_DoesNotBlockOtherFirm()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        // Same end-client name under a DIFFERENT firm — RFC #753 firm
        // scope comes exclusively from the JWT firm claim, so a stale
        // order under FIRM02 must never block FIRM01's reset for the
        // "same" end-client id.
        var endclient = "trader-stale-cross-firm";
        var owner = new EndClientId(endclient);

        var order = new Order(9102UL, owner, "PETR4", 9102UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM02");
        Assert.True(book.TryAdd(order));
        order.MarkWorking();
        Assert.True(order.MarkStale("inbound_gap:3-4", DateTimeOffset.UtcNow));

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // FIRM02's stale order remains completely untouched.
        Assert.True(book.TryGet(9102UL, out var stillThere));
        Assert.NotNull(stillThere);
        Assert.True(stillThere!.IsStale);
    }

    [Fact]
    public async Task Post_NamedSubAccountBucket_IsClearedNotFabricated()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var subAccountPnl = factory.Services.GetRequiredService<SubAccountPnlKeeper>();
        var endclient = "trader-with-named-bucket";

        subAccountPnl.ApplyBucketFill("FIRM01", endclient, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        Assert.NotNull(subAccountPnl.GetBucketAvgCost("FIRM01", endclient, new SubAccountId("SUB1"), "PETR4"));

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Null(subAccountPnl.GetBucketAvgCost("FIRM01", endclient, new SubAccountId("SUB1"), "PETR4"));
        Assert.Null(subAccountPnl.GetBucketAvgCost("FIRM01", endclient, subAccount: null, "PETR4"));
    }

    /// <summary>
    /// #671/#753 code-review addendum #2. Whole-account reset must
    /// clear <see cref="SubAccountPositionKeeper"/> ROWS (not just the
    /// PnL buckets covered by <see cref="Post_NamedSubAccountBucket_IsClearedNotFabricated"/>)
    /// — a named sub-account position row referencing a pre-reset
    /// (NetQuantity, AverageEntryPrice) surviving reset is risk-visible
    /// stale state.
    /// </summary>
    [Fact]
    public async Task Post_NamedSubAccountPosition_IsClearedNotFabricated()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var subAccountPositions = factory.Services.GetRequiredService<SubAccountPositionKeeper>();
        var endclient = "trader-with-named-position";
        var owner = new EndClientId(endclient);

        subAccountPositions.ApplyFill("FIRM01", owner, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        Assert.Single(subAccountPositions.EnumerateForOwner("FIRM01", owner));

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Empty(subAccountPositions.EnumerateForOwner("FIRM01", owner));
    }

    /// <summary>
    /// Firm/client isolation companion to
    /// <see cref="Post_NamedSubAccountPosition_IsClearedNotFabricated"/>:
    /// a named sub-account position row for a DIFFERENT end-client (or
    /// a DIFFERENT firm) must survive this end-client's reset untouched.
    /// </summary>
    [Fact]
    public async Task Post_NamedSubAccountPosition_FirmAndClientIsolation()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var subAccountPositions = factory.Services.GetRequiredService<SubAccountPositionKeeper>();
        var endclient = "trader-iso-target";
        var otherClient = "trader-iso-other-client";
        var owner = new EndClientId(endclient);
        var otherOwner = new EndClientId(otherClient);

        subAccountPositions.ApplyFill("FIRM01", owner, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        subAccountPositions.ApplyFill("FIRM01", otherOwner, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 50, 20m);
        subAccountPositions.ApplyFill("FIRM02", owner, new SubAccountId("SUB1"), "VALE3", OrderSide.Buy, 30, 60m);

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Empty(subAccountPositions.EnumerateForOwner("FIRM01", owner));
        Assert.Single(subAccountPositions.EnumerateForOwner("FIRM01", otherOwner));
        Assert.Single(subAccountPositions.EnumerateForOwner("FIRM02", owner));
    }

    [Fact]
    public async Task Post_ReleasesAllMarginReservations()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Risk:Margin:Enabled"] = "true",
        });
        var issuer = factory.Services.GetRequiredService<B3.Trading.Api.Auth.JwtIssuer>();
        var cashLedger = factory.Services.GetRequiredService<CashLedger>();
        var marginProvider = (ReserveOnSubmitMarginProvider)factory.Services.GetRequiredService<IMarginProvider>();
        var endclient = "trader-with-margin-hold";
        var owner = new EndClientId(endclient);

        cashLedger.ApplyDeposit("FIRM01", owner, 100_000m);
        var decision = await marginProvider.TryReserveAsync(
            55555UL,
            new RiskContext(owner, "FIRM01", "PETR4", OrderSide.Buy, OrderType.Limit, 100, 30m),
            CancellationToken.None);
        Assert.Equal(RiskDecision.Approve, decision);
        Assert.True(marginProvider.ReservedForTesting("FIRM01", endclient) > 0m);

        using var client = factory.CreateClient();
        var (token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync($"/api/admin/accounts/{endclient}/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal(0m, marginProvider.ReservedForTesting("FIRM01", endclient));
    }

    // Mirrors OutboundMutationLedgerTests' own NonTerminalGuardSnapshot
    // helper: constructs a minimal snapshot with Approval left null so
    // Restore's approval-integrity/derived-reconciliation recompute is
    // skipped and the constructed State/RequiresReconciliation values
    // are used verbatim — keeps the guard test focused on the 409 path
    // rather than re-deriving a full approve/attempt/ack event chain.
    private static OutboundMutationSnapshot NonTerminalGuardSnapshot(
        string firmId,
        string endClientRef,
        ulong clOrdId,
        OutboundMutationState state,
        bool requiresReconciliation = false) => new()
        {
            MutationId = new OutboundMutationId(Guid.Parse(
                $"{clOrdId:x8}-5555-6666-7777-888888888888")),
            Kind = OutboundMutationKind.New,
            FirmId = firmId,
            EndClientRef = endClientRef,
            Origin = OutboundMutationOrigin.Rest,
            PrimaryClOrdId = clOrdId,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            State = state,
            StateChangedAtUtc = DateTimeOffset.UtcNow,
            RequiresReconciliation = requiresReconciliation,
            ExplicitlyRequiresReconciliation = requiresReconciliation,
        };
}
