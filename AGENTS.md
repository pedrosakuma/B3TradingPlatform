# AGENTS.md

> Operating guide for AI coding agents (Copilot CLI, Codex, Claude Code, Cursor, etc.) working on this repository. Humans benefit too.

## What this project is

`B3TradingPlatform` is the **participant-side** (corretora / OMS-like) backend
of the B3 exchange-ecosystem family. It owns end-client identity, holds
positions, manages own working orders, and exposes a modern API
(REST + WebSocket + frontend) on top of the raw FIXP/SBE protocol via
[`B3EntryPointClient`](https://github.com/pedrosakuma/B3EntryPointClient).

See [`README.md`](./README.md) for the wider ecosystem map and
[`docs/ARCHITECTURE.md`](./docs/ARCHITECTURE.md) for the longer-form
architecture notes (ER routing, ClOrdID namespacing, etc.).

## Repository layout

```
backend/
  Directory.Packages.props            — central package versions (CPM)
  B3TradingPlatform.slnx              — the only solution file
  src/
    B3.Trading.Domain/                — pure POCOs, value objects, no I/O
    B3.Trading.Application/           — orchestration, risk, algos, persistence
    B3.Trading.Infrastructure/        — IExchangeGateway impl (B3EntryPointClient adapter)
    B3.Trading.Host/                  — composition root, hosted services
    B3.Trading.Api/                   — REST + WebSocket endpoints
    B3.Trading.EntryPointListener/    — FIXP listener for external user bots
  tests/
    B3.Trading.Domain.Tests/          — value-object / invariant tests
    B3.Trading.Application.Tests/     — unit + light integration (in-proc)
    B3.Trading.Api.Tests/             — WebApplicationFactory end-to-end
    B3.Trading.EntryPointListener.Tests/ — FIXP listener / SBE codec tests
    B3.Trading.Conformance/           — real-stack docker-compose conformance suite
frontend/                             — React UI for operators / traders
docker/                               — base + demo + conformance compose overlays
docs/                                 — ARCHITECTURE, CONFORMANCE, METRICS, RUNBOOK, etc.
```

## Build, test, run

All commands are run from the repo root.

```bash
# Build everything
dotnet build B3TradingPlatform.slnx --no-restore -c Release

# Run all tests
dotnet test B3TradingPlatform.slnx --no-restore -c Release

# Run a single test project (preferred during iteration — much faster)
dotnet test backend/tests/B3.Trading.Application.Tests/B3.Trading.Application.Tests.csproj --no-restore

# Run a single test by filter
dotnet test backend/tests/B3.Trading.Api.Tests/B3.Trading.Api.Tests.csproj --no-restore \
  --filter "FullyQualifiedName~AlgoEndpointsTests.PostAlgo_Iceberg_HappyPath"

# Format check (CI gates on this — run before pushing)
dotnet format B3TradingPlatform.slnx --no-restore --verify-no-changes

# Boot the full local stack
docker compose -f docker/docker-compose.yml -f docker/docker-compose.demo.yml up -d
```

**Central package management.** Versions live in `backend/Directory.Packages.props`.
Project files reference packages without a `Version` attribute. Add new packages
to the central props first.

**SDK version.** Pinned in `global.json` — use the SDK from `global.json`,
not whatever is on `PATH`.

## Critical conventions you must respect

These are the easy-to-break things that have cost us debugging time before.

### 🧩 Layered architecture — respect the seam

`Domain` → `Application` → `Infrastructure` → `Host` → `Api`/`EntryPointListener`.
`Domain` and `Application` must not reference `B3.EntryPoint.Client` types
directly — the wire SDK lives behind `IExchangeGateway` in `Infrastructure`
and is wired in `Host`. The SDK 0.15.0 bump (#467) and STP plumbing (#468)
both passed through this seam exactly because the abstraction was respected;
breaking it means rewriting tests every SDK version.

### 🆔 ClOrdID generation and ownership

ClOrdIDs are allocated by `IClOrdIdGenerator` (in `Application`) and are
**per-end-client monotonic with a watermark advance** on every WAL-persisted
mutation. The watermark survives restart (see `OrderCancelService.cs:104`
`AdvanceCounterTo`). **Never** generate a ClOrdID outside the generator;
never reuse one; never assume the ClOrdID is opaque to the venue (RFC
clordid-masking-v0 in #464 documents the threat model and chosen mitigation).

### 🛡️ Risk: defense in depth, opt-in venue enforcement

Risk lives in two layers:

- **App-side pre-trade checks** (`SelfTradePreventionCheck`,
  `PriceCollar`, `MaxQuantity`, `RollingNotional`, etc.) — the primary
  line of defense, always on if configured.
- **Wire-side instructions** (e.g. `SelfTradePreventionInstruction` in
  #468, `MinQty` in #463) — defense-in-depth at the matching engine,
  **opt-in per-firm / per-end-client**. Defaults are intentionally
  permissive (`None`) so a venue-side toggle never silently changes
  historical behavior for tenants that rely on (e.g.) cross-account
  hedging inside the same firm.

When adding a new wire-level risk knob, follow the #468 shape: nullable
enum on `RiskLimits` → resolver with `None` default → mapped in the
gateway → tested via `B3EntryPointClientGatewayMapTests`.

### 📜 RFC-first for ambiguous source-of-truth changes

Any feature whose mapping into our domain is not obvious — CBLC Account
(#458), SDK 0.15.0 splits (`TradingSubAccount`, `InvestorId`,
`RoutingInstruction` in #441), ClOrdID masking (#449), kill-switch
semantics — opens an **RFC issue first**. The investigation lives in the
issue thread; the PR follows the agreed design. This keeps reviewers
focused on the change, not the discovery.

### 🔌 Cross-repo protocol evidence gate

Requests that depend on `B3MatchingPlatform` or `B3MarketDataPlatform`
wire behavior must cite the merged upstream PR or commit that shipped
that behavior, plus the exact official schema version, template ID, and
fields / enums being consumed. Upstream issues, RFCs, and proposal text
are useful discovery links, but are not implementation evidence. If the
merged upstream behavior or official schema mapping is missing, file a
protocol research / blocker issue instead of an implementation request.

This repo is participant-side. Product requirements may describe the
participant capability or user outcome, but must not prescribe venue
messages, EntryPoint / UMDF templates, statuses, fields, or enums that
do not exist in the official schema and a merged upstream implementation.

### 🧪 Flaky tests are real and tracked

Several tests in this repo are timing-sensitive under xunit parallelism
and known to flake (`#332`, `#345`, `#347`, `#316`, others). The protocol:

1. Confirm the failure name matches a known flake by searching open issues.
2. **Rerun the failed job** (`gh run rerun <id> --failed`). If it passes,
   move on. If it fails again with the same signature, comment on the
   tracking issue.
3. **Never** silence a flake by adding `[Skip]` / `Trait` without an issue.
4. When you do fix the root cause (cf. #469 fixing the #347/#345/#316 trio
   via the `LiveChildClOrdId` lock-fence), close every referenced flake
   issue in the PR body.

### 🌐 Conformance suite is real-stack

`backend/tests/B3.Trading.Conformance` runs against an actual docker-compose
stack (matching platform + trading-host + market-data). It is gated by
profile, not by `[Skip]`. Tests fail in CI with timeouts when the trading
behavior under test is genuinely broken — treat conformance failures as
real regressions until proven otherwise. The #468 STP default-mode bug
was caught exactly this way.

### 🐳 Compose overlay variables must be set everywhere

The base `docker/docker-compose.yml` ships without
`Trading__Reports__Cvm__OwnerHashSalt`; the trading-host boot guard
crash-loops in non-Development without it. Demo + conformance overlays
set it; if you add a new overlay, set it there too. Same applies to any
new required environment variable behind a `Required` options validator.

### ✍️ Conventional commits + Copilot trailer

Commits follow conventional commits (`feat(scope): …`, `fix(#N): …`,
`docs: …`, `chore(deps): …`, `style: …`). When the work is co-authored
with an AI agent, append the trailer:

```
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

### 🐚 Shell escapes when driving `gh` / `git`

- **`!` in titles** triggers bash history expansion inside double quotes
  and silently fails. Use single quotes, or `--body-file` for any
  non-trivial message.
- **Don't pipe `gh ... create` output**. On the failure path `gh`
  produces no URL and the pipe masks the failure. Verify with
  `gh pr view` / `gh issue list` after every create.
- **`gh --no-pager` / disable pagers** in scripted contexts.

## How to contribute as an agent

When picking up an issue:

1. **Read the issue body and any linked RFC** before designing.
2. **Build + run the affected test project** before and after.
3. **Keep PRs small and reference the issue** (`Closes #N` or `Refs #N`).
4. **Stack PRs** when a change naturally depends on an unmerged one;
   rebase the stack on the new `main` after each upstream merge to keep
   the diff honest.
5. **Don't commit** secrets, WAL artifacts (`*.wal`), dumps (`*.dmp`),
   or test outputs (`TestResults/`).

### Agent workflow conventions

These are repo-wide meta-workflows that have proven to pay off here. They are
declarative on purpose — the goal is to bias decisions, not to script every
turn. Skip them when the task is genuinely trivial.

- **Mandatory code review before flipping a PR out of draft.** Use the
  `task` tool with `agent_type: "code-review"` and `model: "gpt-5.5"`
  against the staged / branch diff and address every real finding.
  Empirically this catches real bugs on non-trivial PRs in this repo —
  including the STP default-mode regression in #468 that the
  conformance suite caught only after merge of the rebased stack.
- **Decompose-then-parallelise.** Features here often land as several
  small, independent PRs (RFC clordid-masking-v0 shipped as ~11). When
  the work decomposes into ≥2 independent trails (different
  directories, different test surfaces, no shared schema migration),
  prefer dispatching one background sub-agent per trail over
  serialising them in the main loop (Copilot CLI: `task` with
  `mode: "background"`). The main loop keeps coordination + code
  review; the sub-agents own implementation.
- **Pre-scope R&D items with a `research` or `explore` agent first.**
  For fuzzy / multi-week items (e.g. RFC drafts, SDK feature
  archaeology, kill-switch semantics), dispatch a sub-agent for survey
  + feasibility before drafting the plan. Saves the main context for
  actual design + execution.
- **Don't reach for a sub-agent when a single tool call would do.**
  Simple lookups (one grep, one file read), pointed edits, and any
  interactive debugging stay in the main loop — sub-agent fidelity
  loss is not worth it.
- **Worktree etiquette for parallel work.** When dispatching parallel
  sub-agents that touch the same repo, create the branch on the
  remote first (`git worktree add -b <branch> /tmp/<dir> origin/main`)
  so the sub-agent operates on an isolated checkout. After merge,
  `git worktree remove --force <path>` before
  `gh pr merge --delete-branch` (the latter cannot delete a local
  branch held by a worktree).
- **Known-flake rerun protocol.** Before investigating a CI red,
  compare the failing test name against the known-flake list
  (search `is:issue label:flaky-test`). If it matches, rerun the
  failed job once. If it fails again with the same signature on the
  same diff, escalate (don't bury); if it passes, move on.

User- or task-scoped preferences ("for this PR don't run review",
"I prefer option X") belong in the prompt, not here. Conventions in
this section apply to every contributor and every agent on this repo.

## Things deliberately not in scope

- **Modifying `B3EntryPointClient`** — that is an upstream NuGet
  package. Wire-level issues belong in
  [`pedrosakuma/B3EntryPointClient`](https://github.com/pedrosakuma/B3EntryPointClient);
  this repo only adapts.
- **Bypassing `IExchangeGateway`** — every order mutation goes through
  the gateway abstraction so reconciliation, persistence, and
  conformance remain coherent.
- **Per-session state in `Application`** — the WAL is the source of
  truth across restarts; in-memory state is a cache, not the contract.
