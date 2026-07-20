# Conformance scenario inventory

The conformance suite (`backend/tests/B3.Trading.Conformance/`) is the
executable contract for the **participant-side platform** — the API and
WebSocket surface that end-clients and the trader UI consume.

It is the sister of upstream
[`B3.EntryPoint.Conformance`](https://github.com/pedrosakuma/B3EntryPointClient/blob/bootstrap/issue-1/docs/CONFORMANCE.md)
(which is wire-puro, against the FIXP/SBE peer). Same operator
ergonomics — drop env vars, run the same suite against any deployed
instance, ship.

> Component-level tests (in-process `WebApplicationFactory<Program>`)
> live in `backend/tests/B3.Trading.Api.Tests`. Conformance only targets
> a real running process behind real HTTP.

## Configuration

Platform connection is read from environment variables. Tests use
`[ConformanceFact]` and are auto-skipped at discovery time when these
are absent so CI stays green without a deployed instance.

| Variable        | Description                                               |
| --------------- | --------------------------------------------------------- |
| `B3T_BASE_URL`  | Absolute base URL of the platform (e.g. `https://trading.uat.local`) |
| `B3T_AUTH_USER` | Username of a smoke-test account configured on the target |
| `B3T_AUTH_PASS` | Password for that account                                 |
| `B3T_FIXP_ENDPOINT` | Inbound bot listener `host:port`; enables real SOFH/SBE journeys |
| `B3T_FIXP_NEGOTIATE_BURST` | Configured per-credential Negotiate burst used by the rate-limit journey |

To run the suite against a locally-running host:

```bash
export B3T_BASE_URL=http://localhost:5000
export B3T_AUTH_USER=alice
export B3T_AUTH_PASS=correcthorsebatterystaple
dotnet test backend/tests/B3.Trading.Conformance --filter "Category=Conformance"
```

## Inventory

### Bootstrap (this PR)

- **`Spec_HTTP_Auth/HelloLoginTests`** — single happy-path scenario:
  `POST /auth/login` with valid credentials returns a JWT, and that JWT
  is accepted on a protected endpoint (`GET /orders`). Smallest
  possible end-to-end: platform up, JWT pipeline wired, user store
  loaded.

### Real-stack recovery

- **`Spec_FIXP_UserBot/FixpListenerSpecTests`** — creates a one-time bot
  credential over REST, then sends actual SOFH/SBE `Negotiate` and `Establish`
  frames to the listener. It asserts wire acknowledgements, credential reject,
  stale-version `EstablishReject`, and the configured credential rate limit.
  The conformance compose overlay always sets the endpoint, so CI cannot pass
  these rows through an environment-only placeholder or zero-traffic skip.

- **`Spec_FIXP_SessionRoll/SuspendedTimeoutBoundarySpecTests`** —
  transport-fault boundary coverage for the venue FIXP suspend window:
  disconnect the matching-platform TCP leg **within**
  `SuspendedTimeoutMs` and assert the order re-syncs without a stale
  flag; disconnect **past** the timeout and assert the surviving order
  is flagged stale after renegotiation. Both recovery paths then submit
  a fresh post-reconnect crossed pair and assert trading is genuinely
  back: the new order becomes `Working` in `GET /orders`, then both legs
  transition to `Filled` (note: `GET /orders` is full history, so
  "leaves the book" is asserted as `Working` → terminal, not literal
  disappearance from the response). Requires the real-stack sandbox
  (`B3T_REAL_STACK_CONFORMANCE=true`) plus docker CLI/socket access for
  the test process (`B3T_DOCKER_CONTROL=true`; the
  `docker-compose.real-conformance.yml` overlay wires this automatically).
- **`Spec_HTTP_MarketData/MarketDataOutageSpecTests`** — marketdata-leg
  resilience on the real stack: sever the `b3-marketdata` container's
  `b3-net` attachment, prove
  `GET /admin/marketdata/reference-prices` stays on the last-known-good
  live cache instead of crashing, prove `POST /orders` / matching fills
  still work while the feed is down, then reconnect and assert a fresh
  crossed trade advances the live ref-price again. Pairs with the
  operational guidance in
  [`docs/operations/runbook-failover-recovery.md`](operations/runbook-failover-recovery.md)
  §1.8.
- **`Spec_Recovery/TradingHostCrashRestartSpecTests`** — hard-crash
  recovery coverage for the participant host itself: `docker kill -s
  SIGKILL b3-trading-host`, wait for the host to be down, then restart it
  and prove two
  sibling contracts: (a) a pre-crash resting order still comes back with
  the same working-state leaves/cum quantities, pre-crash
  cash/position/realized-PnL state from a real fill survived WAL replay,
  `/admin/firms` shows both FIRM01 and FIRM02 FIXP sessions back in
  `established`, and a fresh post-restart crossed pair still trades
  through to `Filled`; and (b) an external FIXP counterparty on session
  `10102` can fill a trading-host-owned order **while the host is down**,
  with the missed ER replayed on restart so `GET /orders` shows the
  correct terminal fill instead of a stale `Working` snapshot. Also uses
  the docker-control gate and real-stack sandbox overlay.
- **`Spec_FIXP_SessionRoll/MatchingPlatformRestartSpecTests`** —
  process-fault sibling of the scenario above: restart the
  `matching-platform` container itself (not just its TCP leg), assert the
  host is forced onto the `Renegotiated`/advanced-`SessionVerId` path and
  stale-flags the pre-restart survivor, then contract-prove the venue's
  book/WAL survived by crossing that stale survivor into a real `Filled`
  terminal ER before finally asserting a fresh post-restart order round-trip
  still trades through to `Filled`.

### Backlog (separate scenarios; add as the contract solidifies)

- **`Spec_HTTP_Orders/`** — `POST /orders` happy path + validation
  errors (missing fields, invalid side/type, `securityId == 0`,
  negative qty), `GET /orders` listing, `DELETE /orders/{id}` flow.
- **`Spec_HTTP_Risk/`** — kill-switch toggle round-trip, fat-finger
  rejection (price collar / max qty / max notional), position-limit
  rejection, all surfacing as synthetic ERs with the same shape as
  exchange rejections.
- **`Spec_WS_Subscribe/`** — connect with `?access_token=`, subscribe
  to `executions.me`, receive snapshot-then-deltas with monotonic
  sequence numbers; reconnect with `lastSeq` resumes losslessly.
- **`Spec_WS_Backpressure/`** — slow-consumer disconnect when the
  outbound ring saturates.
- **`Spec_Multi_Firm/`** — orders submitted under a JWT scoped to
  firm A do not appear in WebSocket fan-out subscribed under firm B.
- **`Spec_Lifecycle/`** — `/health`, `/ready`, `/live` shape;
  SIGTERM drain (`/ready` flips to 503; in-flight `POST /orders`
  completes; new `POST /orders` returns 503; WAL flushes; final
  snapshot lands).

Add a new scenario by:

1. Picking the right `Spec_<area>/` folder (create one if needed).
2. Writing one `[ConformanceFact]` per testable requirement; one
   assertion per scenario, contract-level only — no white-box.
3. Updating this inventory.

## Durable outbound mutation release gate (#648)

Outbound crash recovery is a release-blocking contract. The `Docker` workflow's
`Outbound recovery conformance release gate` check depends on unavailable-mode
conformance, the backup/recovery drill, and real-stack conformance. A failure is
not an allowed flaky-test skip and prevents candidate promotion.

### RFC §9 crash matrix

`OutboundCrashMatrixReleaseGateTests` is the executable inventory: it has one
named gate for C1-C25 and fails if any mapped behavioral test is removed,
renamed, or skipped.

| Row | Behavioral test |
| --- | --- |
| C1 | `RestOrderIdempotencyStoreTests.SnapshotRestore_PreservesReplayAndConflictSemantics` |
| C2 | `CommittedPrefixFileEventStoreTests.CrashBeforeMarkerPublication_DoesNotReplaySurvivor` |
| C3 | `DurableOrderSubmissionServiceTests.ApprovedSubmit_CommitsPendingApprovalIntentFrameAndWriteInOrder` |
| C4 | `DurableOrderSubmissionServiceTests.RiskReject_IsDurableBeforeApprovalAndNeverEntersGateway` |
| C5 | `DurableOrderSubmissionServiceTests.ApprovalAppendFailure_TerminalisesNoWriteBeforeMarginRelease` |
| C6 | `NewOrderOutboundCoordinatorTests.RecoveryStart_EntersApprovedMutationExactlyOnce` |
| C7 | `CommittedPrefixFileEventStoreTests.CrashBeforeMarkerPublication_DoesNotReplaySurvivor` |
| C8 | `OutboundMutationLedgerTests.ColdStartCoordinator_CommitsIntentOnlyProvenUnsent_AndDoesNotResendFramePrepared` |
| C9 | `NewOrderOutboundCoordinatorTests.FramePersistenceFailure_PreventsWriteAndRequiresReconciliation` |
| C10 | `OutboundMutationLedgerTests.ColdStartCoordinator_CommitsIntentOnlyProvenUnsent_AndDoesNotResendFramePrepared` |
| C11 | `NewOrderOutboundCoordinatorTests.TypedPreFrameFailure_IsProvenUnsentAndRetainsMarginUntilDomainTerminalCommit` |
| C12-C13 | `NewOrderOutboundCoordinatorTests.ExceptionAfterFrame_IsAmbiguousAndDoesNotReleaseMargin` |
| C14 | `CommittedPrefixFileEventStoreTests.CrashBeforeMarkerPublication_DoesNotReplaySurvivor` |
| C15 | `OutboundMutationLedgerTests.Recovery_IntentOnlyIsProvenUnsent_FrameAndWriteAreAmbiguous` |
| C16-C18 | `OutboundMutationLedgerTests.CommitBeforeApply_CrashWindowReplaysEvidenceDeterministically` |
| C19 | `OutboundMutationLedgerTests.BusinessReject_CorrelatesOnlyExactFirmSessionVersionAndSequence` |
| C20 | `OutboundMutationLedgerTests.BusinessReject_MissingIdentityRemainsUnmatchedAndDoesNotUseText` |
| C21 | `OutboundMutationLedgerTests.NotApplied_UsesOverflowSafeHalfOpenRange_AndNeverAutoResends` |
| C22 | `OutboundMutationLedgerTests.RecoveryGate_BlocksOnlyFirmsCapturedDuringColdClassification` |
| C23 | `CommittedPrefixFileEventStoreTests.MarkerFault_IsStickyAndFailsEveryOutstandingFence` |
| C24 | `SnapshotCommittedPrefixTests.Recovery_IgnoresOnDiskSnapshotAheadOfCommittedMarker` |
| C25 | `ActiveHostFenceTests.SecondHostLoses_AndNextExclusiveAcquisitionAdvancesDurableEpoch` |

The marker-prefix property is
`OutboundMutationLedgerTests.Property_RestoredPrefixPlusTail_EqualsFullCommittedPrefix`;
concurrent snapshot capture is separately checked by
`ConcurrentSnapshotCapture_AlwaysRestoresACommittedLedgerPrefix`.

### RFC §20 and real-stack evidence

| Contract | Executable evidence |
| --- | --- |
| Commit-before-gateway and O2 callback boundary | `ApprovedMutation_CommitsIntentFrameAndWriteBeforeReturning`, `FramePersistenceFailure_PreventsWriteAndRequiresReconciliation` |
| Finite fresh-ClOrdID attempts | `RetryAfterProvenUnsent_RequiresFreshAttemptAndClOrdId_AndIsFinite`, `ProvenUnsent_RetryUsesFreshId_PreservesTombstones_AndCapsAttempts` |
| No synthetic reject after ambiguous gateway failure | `ExceptionAfterFrame_IsAmbiguousAndDoesNotReleaseMargin` |
| Exact ER/BusinessReject/NotApplied correlation | `BusinessReject_CorrelatesOnlyExactFirmSessionVersionAndSequence`, `NotApplied_UsesOverflowSafeHalfOpenRange_AndNeverAutoResends` |
| Same-session reattach and post-recovery trading | `SuspendedTimeoutBoundarySpecTests.WithinSuspendedTimeout_Reattaches_OrderSurvivesNoStaleFlag` |
| Late/retransmitted ER converges once | `TradingHostCrashRestartSpecTests.SigKillRestart_FillDuringOutage_ReplaysMissedExecutionReport`, `ExecutionReport_DuplicatePossResendAndConflictingSameIdentityAreMonotonic` |
| Rolled session remains evidence-conservative | `MatchingPlatformRestartSpecTests.Restart_Renegotiates_SurvivingOrderFlaggedStale_BookSurvives_FreshTradingRecovers`, `ManualAnnotationAndSessionRollEvidence_NeverReleaseCapacity` |
| Authoritative operator resolution | `AuthoritativeEvidence_RequiresDistinctCheckerAndReleasesCapacity` |
| Manual absence/risk release rejected | `ManualAnnotationAndSessionRollEvidence_NeverReleaseCapacity` |
| Durable REST/FIXP identities | `OrderIdempotencyEndpointTests.SameKeyAfterRestart_ReplaysDurableBinding`, `FixpOrderAdapterFailClosedTests.TombstonedCancelId_RejectsBeforeCancelPipeline` |
| Startup ingress stays closed | `OutboundRecoveryReadinessTests.ClosedRecoveryGate_RejectsAlgoAndOrderMutationsBeforeStateAccess` |
| Sensitive artifact absence | `SerializationAndDiagnostics_NeverExposeSensitivePlaintext`, `SnapshotAndAuditPayloads_NeverContainSensitivePlaintext`, `AdminOutboundMutationEndpointTests.Timelines_AreFirmScopedAndRedacted`, `LogCapture_ContainsOnlyCountsAndNeverCustomerValues` |

V0 deliberately does not claim exact same-sequence outbound replay. The SDK
does not expose a supported original-frame replay operation; the accepted
contract is same-session inbound retransmission plus fail-closed ambiguity, as
recorded in RFC §18.7 and B3EntryPointClient#223.
