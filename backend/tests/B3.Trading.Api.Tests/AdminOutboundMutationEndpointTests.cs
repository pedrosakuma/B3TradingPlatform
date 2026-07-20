using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using xRetry;

namespace B3.Trading.Api.Tests;

public sealed class AdminOutboundMutationEndpointTests
{
    private const string AccountSecret = "ACCOUNT-PII-647";
    private const string InvestorSecret = "INVESTOR-PII-647";
    private const string EndClientSecret = "ENDCLIENT-PII-647";
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 20, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Timelines_AreFirmScopedAndRedacted()
    {
        using var factory = NewFactory(out _);
        var fixture = SeedAmbiguousMutation(factory, "F1", 64701);
        using var firmA = CreateAdminClient(factory, "firm-a-admin", "F1");
        using var firmB = CreateAdminClient(factory, "firm-b-admin", "F2");

        var list = await firmA.GetAsync("/admin/outbound-mutations/");
        var detail = await firmA.GetAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}");
        var foreignDetail = await firmB.GetAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}");
        var foreignList = await firmA.GetAsync(
            "/admin/outbound-mutations/?firmId=F2");
        var foreignEvidence = await firmB.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/evidence",
            EvidenceRegistrationBody("official_extract", '0'));
        var health = await firmA.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignDetail.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignEvidence.StatusCode);
        using var healthJson = JsonDocument.Parse(
            await health.Content.ReadAsStringAsync());
        var outboundRecovery = healthJson.RootElement.GetProperty("outboundRecovery");
        Assert.Equal(1, outboundRecovery.GetProperty("unresolvedMutationCount").GetInt32());
        Assert.Equal(1, outboundRecovery.GetProperty("unresolvedFirmCount").GetInt32());
        Assert.True(outboundRecovery.TryGetProperty(
            "oldestAmbiguityAgeSeconds",
            out _));
        var payload = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AccountSecret, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(InvestorSecret, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(EndClientSecret, payload, StringComparison.Ordinal);
        Assert.Contains(fixture.EndClientRef, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertextBase64", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AlgoReconciliationRequired_IsListedAndResolvableByAdminWorkflow()
    {
        using var factory = NewFactory(out _);
        var fixture = SeedAlgoProvenUnsentMutation(factory, "F1", 64711);
        var ledger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        ledger.Apply(new OutboundReconciliationRequiredEvent
        {
            MutationId = fixture.MutationId,
            Reason = "AlgoRepegAttemptCapExhausted",
            TimestampUtc = T0.AddMinutes(1),
        });
        using var admin = CreateAdminClient(factory, "algo-operator", "F1");

        var list = await admin.GetAsync(
            "/admin/outbound-mutations/?requiresReconciliation=true");
        var listPayload = await list.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(fixture.MutationId.ToString(), listPayload, StringComparison.Ordinal);
        Assert.Contains("\"origin\":\"algo\"", listPayload, StringComparison.Ordinal);
        Assert.Contains("\"requiresReconciliation\":true", listPayload, StringComparison.Ordinal);

        var reference = await PrepareEvidenceAsync(
            factory,
            fixture,
            "official_extract",
            admin);
        var resolve = await admin.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_absent",
                evidenceType = "official_extract",
                evidenceReference = reference,
                reason = "official_extract_attested",
            });

        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        using var result = JsonDocument.Parse(await resolve.Content.ReadAsStringAsync());
        Assert.False(result.RootElement.GetProperty("requiresReconciliation").GetBoolean());
        var mutation = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(OutboundMutationOrigin.Algo, mutation.Origin);
        Assert.Equal(OutboundMutationState.OperatorResolved, mutation.State);
        Assert.False(mutation.RequiresReconciliation);
        Assert.False(mutation.ExplicitlyRequiresReconciliation);
    }

    [Theory]
    [InlineData("contracted_not_applied", "contracted_not_applied_verified")]
    [InlineData("venue_mass_action", "venue_mass_action_verified")]
    [InlineData("official_extract", "official_extract_attested")]
    public async Task AuthoritativeEvidence_RequiresDistinctCheckerAndReleasesCapacity(
        string evidenceType,
        string reason)
    {
        using var factory = NewFactory(out var margin);
        var fixture = SeedAmbiguousMutation(factory, "F1", 64702);
        using var maker = CreateAdminClient(factory, "maker", "F1");
        using var checker = CreateAdminClient(factory, "checker", "F1");
        var reference = await PrepareEvidenceAsync(
            factory,
            fixture,
            evidenceType,
            maker);

        var propose = await maker.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_absent",
                evidenceType,
                evidenceReference = reference,
                reason,
            });

        Assert.Equal(HttpStatusCode.Accepted, propose.StatusCode);
        Assert.Equal(0, margin.ReleaseCount);
        using var proposal = JsonDocument.Parse(
            await propose.Content.ReadAsStringAsync());
        var proposalId = proposal.RootElement.GetProperty("proposalId").GetString();
        var selfApprove = await maker.PostAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve/{proposalId}/approve",
            content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, selfApprove.StatusCode);
        Assert.Equal(0, margin.ReleaseCount);

        var approve = await checker.PostAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve/{proposalId}/approve",
            content: null);

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal(1, margin.ReleaseCount);

        var duplicate = await maker.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_absent",
                evidenceType,
                evidenceReference = reference,
                reason,
            });

        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(1, margin.ReleaseCount);
    }

    [Fact]
    public async Task VenueAcknowledgedMutation_CannotBeResolvedVenueAbsent()
    {
        using var factory = NewFactory(out _);
        var fixture = SeedAmbiguousMutation(factory, "F1", 64708);
        var ledger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        ledger.ApplyVenueAcknowledgement(new ExecutionReportReceivedEvent
        {
            ClOrdId = fixture.ClOrdId,
            ExecKind = "Fill",
            LeavesQuantity = 0,
            CumulativeQuantity = 10,
            LastQuantity = 10,
            LastPrice = 30m,
            Synthetic = false,
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            InboundSeqNum = 91,
            VenueSendingTime = T0.AddMinutes(1),
            TimestampUtc = T0.AddMinutes(1),
        });
        var evidence = Assert.Single(
            ledger.GetInboundEvidenceForMutation(fixture.MutationId));
        using var admin = CreateAdminClient(factory, "maker", "F1");

        var response = await admin.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_absent",
                evidenceType = "terminal_er",
                evidenceReference = evidence.EvidenceId,
                reason = "terminal_er_verified",
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var mutation = ledger.SnapshotMutations().Single(
            item => item.MutationId == fixture.MutationId);
        Assert.Equal(OutboundMutationState.VenueAcknowledged, mutation.State);
        Assert.Empty(mutation.ResolutionProposals);
    }

    [Theory]
    [InlineData("venue_mass_action", "venue_mass_action_verified", '7')]
    [InlineData("official_extract", "official_extract_attested", '8')]
    public async Task BareExternalDigest_IsRejectedUntilCoveringEvidenceIsRegistered(
        string evidenceType,
        string reason,
        char digestCharacter)
    {
        using var factory = NewFactory(out _);
        var fixture = SeedAmbiguousMutation(factory, "F1", 64707);
        using var maker = CreateAdminClient(factory, "maker", "F1");
        var prefix = evidenceType == "venue_mass_action"
            ? "venue-report:"
            : "official-extract:";
        var reference = $"{prefix}{new string(digestCharacter, 64)}";

        var bare = await maker.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_absent",
                evidenceType,
                evidenceReference = reference,
                reason,
            });
        var nonCovering = await maker.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/evidence",
            new
            {
                sourceType = evidenceType,
                evidenceReference = reference,
                coverageStartUtc = T0.AddDays(1),
                coverageEndUtc = T0.AddDays(2),
                attestationReference =
                    $"attestation:{new string(digestCharacter, 64)}",
            });
        var registered = await maker.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/evidence",
            EvidenceRegistrationBody(evidenceType, digestCharacter));
        var proposed = await maker.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_absent",
                evidenceType,
                evidenceReference = reference,
                reason,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bare.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nonCovering.StatusCode);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, proposed.StatusCode);
    }

    [Fact]
    public async Task ManualAnnotationAndSessionRollEvidence_NeverReleaseCapacity()
    {
        using var factory = NewFactory(out var margin);
        var fixture = SeedAmbiguousMutation(factory, "F1", 64703);
        using var admin = CreateAdminClient(factory, "maker", "F1");

        var annotation = await admin.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "leave_ambiguous",
                evidenceType = "manual_annotation",
                evidenceReference = $"annotation:{new string('a', 64)}",
                reason = "manual_comparison_recorded",
            });
        var invalidTerminal = await admin.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_absent",
                evidenceType = "manual_annotation",
                evidenceReference = $"annotation:{new string('b', 64)}",
                reason = "manual_comparison_recorded",
            });
        var sessionRoll = await admin.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "leave_ambiguous",
                evidenceType = "manual_annotation",
                evidenceReference = $"annotation:{new string('c', 64)}",
                reason = "session_roll",
            });

        Assert.Equal(HttpStatusCode.OK, annotation.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidTerminal.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sessionRoll.StatusCode);
        Assert.Equal(0, margin.ReleaseCount);
        foreach (var response in new[] { annotation, invalidTerminal, sessionRoll })
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(AccountSecret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(InvestorSecret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(EndClientSecret, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AuditFailure_Returns503AndRetainsAmbiguity()
    {
        var failingAudit = new FailingAuditLogger();
        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            services =>
            {
                services.RemoveAll<IAuditLogger>();
                services.AddSingleton<IAuditLogger>(failingAudit);
                services.RemoveAll<IMarginProvider>();
                services.AddSingleton<IMarginProvider, RecordingMargin>();
            });
        var fixture = SeedAmbiguousMutation(factory, "F1", 64704);
        SeedRegisteredEvidence(factory, fixture, "official_extract", 'd');
        using var admin = CreateAdminClient(factory, "maker", "F1");

        var response = await admin.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_absent",
                evidenceType = "official_extract",
                evidenceReference = $"official-extract:{new string('d', 64)}",
                reason = "official_extract_attested",
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(failingAudit.CommittedCalls > 0);
        var mutation = factory.Services.GetRequiredService<OutboundMutationLedger>()
            .SnapshotMutations()
            .Single(candidate => candidate.MutationId == fixture.MutationId);
        Assert.Empty(mutation.ResolutionProposals);
        Assert.Empty(mutation.OperatorEvidence);
        Assert.True(mutation.RequiresReconciliation);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AccountSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(InvestorSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(EndClientSecret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LateContradictoryExecutionReport_ReopensTimelineAndRaisesAlertMetric()
    {
        using var factory = NewFactory(out var margin);
        var fixture = SeedAmbiguousMutation(factory, "F1", 64706);
        using var maker = CreateAdminClient(factory, "maker", "F1");
        using var checker = CreateAdminClient(factory, "checker", "F1");
        var registered = await maker.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/evidence",
            EvidenceRegistrationBody("official_extract", '9'));
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var proposed = await maker.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_absent",
                evidenceType = "official_extract",
                evidenceReference = $"official-extract:{new string('9', 64)}",
                reason = "official_extract_attested",
            });
        using var proposal = JsonDocument.Parse(
            await proposed.Content.ReadAsStringAsync());
        var proposalId = proposal.RootElement.GetProperty("proposalId").GetString();
        Assert.Equal(
            HttpStatusCode.OK,
            (await checker.PostAsync(
                $"/admin/outbound-mutations/{fixture.MutationId}/resolve/{proposalId}/approve",
                content: null)).StatusCode);

        var alertCount = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Name == "trading.outbound.contradictory_evidence_total")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
            alertCount += measurement);
        listener.Start();
        var mock = Assert.IsType<MockEntryPointClient>(
            factory.Services.GetRequiredService<IEntryPointClient>());
        mock.EmitExecutionReport(new ExecutionReportEnvelope(
            fixture.ClOrdId,
            EpExecType.Fill,
            LeavesQuantity: 0,
            CumulativeQuantity: 10,
            LastQuantity: 10,
            LastPrice: 30m,
            RejectReason: null,
            FirmId: "F1",
            SessionId: 11,
            SessionVerId: 3,
            InboundSeqNum: 99,
            SendingTime: T0.AddMinutes(5)));

        var detail = await checker.GetAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}");
        var body = await detail.Content.ReadAsStringAsync();
        Assert.Contains("\"state\":\"ambiguous\"", body, StringComparison.Ordinal);
        Assert.Contains("\"requiresReconciliation\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"disposition\":\"conflicting\"", body, StringComparison.Ordinal);
        Assert.Contains(
            "\"authoritativeTerminalContradiction\":true",
            body,
            StringComparison.Ordinal);
        Assert.Equal(1, alertCount);
        Assert.DoesNotContain(AccountSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(InvestorSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(EndClientSecret, body, StringComparison.Ordinal);

        using var detailJson = JsonDocument.Parse(body);
        var lateEvidenceId = detailJson.RootElement
            .GetProperty("inboundEvidence")[0]
            .GetProperty("evidenceId")
            .GetString();
        var invalidAbsent = await maker.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_absent",
                evidenceType = "terminal_er",
                evidenceReference = lateEvidenceId,
                reason = "terminal_er_verified",
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidAbsent.StatusCode);
        Assert.Equal(1, margin.ReleaseCount);
        var followUp = await checker.PostAsJsonAsync(
            $"/admin/outbound-mutations/{fixture.MutationId}/resolve",
            new
            {
                decision = "venue_acknowledged",
                evidenceType = "terminal_er",
                evidenceReference = lateEvidenceId,
                reason = "late_contradiction_reconciled",
            });
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    [RetryFact(maxRetries: 3, delayBetweenRetriesMs: 100)]
    public void ReconciliationMetrics_HaveOnlyBoundedCategoricalLabels()
    {
        using var factory = NewFactory(out _);
        SeedAmbiguousMutation(factory, "F1", 64705);
        _ = factory.CreateClient();
        var ledger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        var snapshots = ledger.GetReconciliationMetrics(DateTimeOffset.UtcNow);
        var observed = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "B3.Trading"
                && instrument.Name.StartsWith(
                    "trading.outbound.",
                    StringComparison.Ordinal))
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            observed.Add(tags.ToArray()));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            observed.Add(tags.ToArray()));
        listener.Start();
        MetricsRegistry.RegisterOutboundReconciliationSource(() => snapshots);
        listener.RecordObservableInstruments();

        Assert.NotEmpty(observed);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "firm",
            "kind",
            "age_bucket",
            "ambiguity_reason",
        };
        Assert.All(observed.SelectMany(tags => tags), tag =>
        {
            Assert.Contains(tag.Key, allowed);
            Assert.DoesNotContain("clord", tag.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("account", tag.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(EndClientSecret, tag.Value?.ToString() ?? string.Empty);
        });
    }

    private static TestAppFactory NewFactory(out RecordingMargin margin)
    {
        var recording = new RecordingMargin();
        margin = recording;
        return TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            services =>
            {
                services.RemoveAll<IMarginProvider>();
                services.AddSingleton<IMarginProvider>(recording);
            });
    }

    private static HttpClient CreateAdminClient(
        TestAppFactory factory,
        string subject,
        string firm)
    {
        var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var token = issuer.Issue(subject, Roles.Admin, firm).Token;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static SeededMutation SeedAmbiguousMutation(
        TestAppFactory factory,
        string firmId,
        ulong clOrdId)
    {
        _ = factory.CreateClient();
        var ledger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        var protector = factory.Services.GetRequiredService<IOutboundCommandProtector>();
        var mutationId = OutboundMutationId.New();
        var attemptId = OutboundAttemptId.New();
        var canonical = new OutboundCanonicalCommand
        {
            ClOrdId = clOrdId,
            SecurityId = 123,
            Symbol = "PETR4",
            Side = "Buy",
            OrderType = "Limit",
            Quantity = 10,
            Price = 30m,
        };
        var sensitive = new SensitiveOutboundCommand
        {
            Account = AccountSecret,
            InvestorId = InvestorSecret,
            EndClientId = EndClientSecret,
        };
        var approval = OutboundApprovalFactory.Create(
            mutationId,
            firmId,
            canonical,
            sensitive,
            [
                OutboundSensitiveFieldRef.Account,
                OutboundSensitiveFieldRef.InvestorId,
                OutboundSensitiveFieldRef.EndClientId,
            ],
            protector,
            T0);
        var endClientRef = protector.CreateStableEndClientRef(firmId, EndClientSecret);
        ledger.Apply(new OutboundApprovedEvent
        {
            MutationId = mutationId,
            MutationKind = OutboundMutationKind.New,
            FirmId = firmId,
            EndClientRef = endClientRef,
            Origin = OutboundMutationOrigin.Rest,
            PrimaryClOrdId = clOrdId,
            RecordedAtUtc = T0,
            Approval = approval,
            TimestampUtc = T0,
        });
        ledger.Apply(new OutboundAttemptIntentPreparedEvent
        {
            MutationId = mutationId,
            AttemptId = attemptId,
            AttemptNo = 1,
            ClOrdId = clOrdId,
            ProcessEpochId = ProcessEpochId.New(),
            IntentPreparedAtUtc = T0.AddSeconds(1),
            TimestampUtc = T0.AddSeconds(1),
        });
        ledger.Apply(new OutboundFramePreparedEvent
        {
            MutationId = mutationId,
            AttemptId = attemptId,
            FirmId = firmId,
            SessionId = 11,
            SessionVerId = 2,
            OutboundSeqNum = clOrdId,
            EncodedFrameSha256 = new string('f', 64),
            PreparedAtUtc = T0.AddSeconds(2),
            TimestampUtc = T0.AddSeconds(2),
        });
        ledger.MarkAmbiguous(
            mutationId,
            attemptId,
            OutboundAmbiguityReason.GatewayOutcomeUnknown,
            T0.AddSeconds(3));
        return new SeededMutation(mutationId, attemptId, clOrdId, endClientRef);
    }

    private static SeededMutation SeedAlgoProvenUnsentMutation(
        TestAppFactory factory,
        string firmId,
        ulong clOrdId)
    {
        _ = factory.CreateClient();
        var ledger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        var protector = factory.Services.GetRequiredService<IOutboundCommandProtector>();
        var mutationId = OutboundMutationId.New();
        var attemptId = OutboundAttemptId.New();
        var canonical = new OutboundCanonicalCommand
        {
            ClOrdId = clOrdId,
            SecurityId = 123,
            Symbol = "PETR4",
            Side = "Buy",
            OrderType = "Limit",
            Quantity = 10,
            Price = 30m,
        };
        var sensitive = new SensitiveOutboundCommand
        {
            Account = AccountSecret,
            InvestorId = InvestorSecret,
            EndClientId = EndClientSecret,
        };
        var approval = OutboundApprovalFactory.Create(
            mutationId,
            firmId,
            canonical,
            sensitive,
            [
                OutboundSensitiveFieldRef.Account,
                OutboundSensitiveFieldRef.InvestorId,
                OutboundSensitiveFieldRef.EndClientId,
            ],
            protector,
            T0);
        var endClientRef = protector.CreateStableEndClientRef(firmId, EndClientSecret);
        ledger.Apply(new OutboundApprovedEvent
        {
            MutationId = mutationId,
            MutationKind = OutboundMutationKind.New,
            FirmId = firmId,
            EndClientRef = endClientRef,
            Origin = OutboundMutationOrigin.Algo,
            AlgoOriginIdentity = new AlgoOutboundOriginIdentity(
                ParentAlgoId: 647,
                ActionKind: AlgoOutboundActionKind.NewChild,
                Sequence: 1),
            PrimaryClOrdId = clOrdId,
            RecordedAtUtc = T0,
            Approval = approval,
            TimestampUtc = T0,
        });
        ledger.Apply(new OutboundAttemptIntentPreparedEvent
        {
            MutationId = mutationId,
            AttemptId = attemptId,
            AttemptNo = 1,
            ClOrdId = clOrdId,
            ProcessEpochId = ProcessEpochId.New(),
            IntentPreparedAtUtc = T0.AddSeconds(1),
            TimestampUtc = T0.AddSeconds(1),
        });
        ledger.Apply(new OutboundProvenUnsentEvent
        {
            MutationId = mutationId,
            AttemptId = attemptId,
            Evidence = OutboundProvenUnsentEvidence.TypedPreFrameFailure,
            TimestampUtc = T0.AddSeconds(2),
        });
        return new SeededMutation(mutationId, attemptId, clOrdId, endClientRef);
    }

    private static async Task<string> PrepareEvidenceAsync(
        TestAppFactory factory,
        SeededMutation mutation,
        string evidenceType,
        HttpClient admin)
    {
        var ledger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        if (evidenceType == "terminal_er")
        {
            ledger.ApplyVenueAcknowledgement(new ExecutionReportReceivedEvent
            {
                ClOrdId = mutation.ClOrdId,
                ExecKind = "Rejected",
                LeavesQuantity = 0,
                CumulativeQuantity = 0,
                LastQuantity = 0,
                LastPrice = 0m,
                RejectReason = "VENUE_REJECTED",
                Synthetic = false,
                FirmId = "F1",
                SessionId = 11,
                SessionVerId = 2,
                InboundSeqNum = 90,
                VenueSendingTime = T0.AddMinutes(1),
                TimestampUtc = T0.AddMinutes(1),
            });
            return ledger.GetInboundEvidenceForMutation(mutation.MutationId)
                .Single()
                .EvidenceId;
        }
        if (evidenceType == "contracted_not_applied")
        {
            ledger.ApplyNotApplied(new NotAppliedReceivedEvent
            {
                FirmId = "F1",
                SessionId = 11,
                SessionVerId = 2,
                FromSeqNo = mutation.ClOrdId,
                Count = 1,
                ObservedAtUtc = T0.AddMinutes(1),
                TimestampUtc = T0.AddMinutes(1),
            });
            return ledger.GetInboundEvidenceForMutation(mutation.MutationId)
                .Single()
                .EvidenceId;
        }
        var reference = evidenceType == "venue_mass_action"
            ? $"venue-report:{new string('e', 64)}"
            : $"official-extract:{new string('f', 64)}";
        var response = await admin.PostAsJsonAsync(
            $"/admin/outbound-mutations/{mutation.MutationId}/evidence",
            EvidenceRegistrationBody(
                evidenceType,
                evidenceType == "venue_mass_action" ? 'e' : 'f'));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return reference;
    }

    private static object EvidenceRegistrationBody(
        string sourceType,
        char digestCharacter) => new
        {
            sourceType,
            evidenceReference = sourceType == "venue_mass_action"
                ? $"venue-report:{new string(digestCharacter, 64)}"
                : $"official-extract:{new string(digestCharacter, 64)}",
            coverageStartUtc = T0.AddHours(-1),
            coverageEndUtc = T0.AddHours(1),
            attestationReference =
                $"attestation:{new string(digestCharacter, 64)}",
        };

    private static void SeedRegisteredEvidence(
        TestAppFactory factory,
        SeededMutation mutation,
        string sourceType,
        char digestCharacter)
    {
        var ledger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        var venueMassAction = sourceType == "venue_mass_action";
        var prefix = venueMassAction ? "venue-report:" : "official-extract:";
        ledger.Apply(new OutboundAuthoritativeEvidenceRegisteredEvent
        {
            MutationId = mutation.MutationId,
            Evidence = new OutboundAuthoritativeEvidenceSnapshot
            {
                EvidenceReference =
                    $"{prefix}{new string(digestCharacter, 64)}",
                EvidenceDigest = new string(digestCharacter, 64),
                FirmId = "F1",
                SourceType = venueMassAction
                    ? OutboundAuthoritativeEvidenceSourceType.VenueMassAction
                    : OutboundAuthoritativeEvidenceSourceType.OfficialExtract,
                CoverageStartUtc = T0.AddHours(-1),
                CoverageEndUtc = T0.AddHours(1),
                CoveredMutationIds = [mutation.MutationId],
                AttestationReference =
                    $"attestation:{new string(digestCharacter, 64)}",
                AttestedBy = "evidence-attestor",
                AttestedAtUtc = T0.AddMinutes(1),
                RegisteredAtUtc = T0.AddMinutes(1),
            },
            TimestampUtc = T0.AddMinutes(1),
        });
    }

    private sealed record SeededMutation(
        OutboundMutationId MutationId,
        OutboundAttemptId AttemptId,
        ulong ClOrdId,
        string EndClientRef);

    private sealed class RecordingMargin : IMarginProvider
    {
        public int ReleaseCount { get; private set; }

        public Task<RiskDecision> TryReserveAsync(
            ulong clOrdId,
            RiskContext ctx,
            CancellationToken ct) =>
            Task.FromResult(RiskDecision.Approve);

        public void ReleaseReservation(ulong clOrdId) => ReleaseCount++;
    }

    private sealed class FailingAuditLogger : IAuditLogger
    {
        public int CommittedCalls { get; private set; }
        public void Log(AuditLogEvent evt) { }
        public void LogOrFail(AuditLogEvent evt) { }
        public void LogCommittedOrFail(
            AuditLogEvent evt,
            CancellationToken cancellationToken = default)
        {
            CommittedCalls++;
            throw new WalBackpressureException("injected");
        }
    }
}
