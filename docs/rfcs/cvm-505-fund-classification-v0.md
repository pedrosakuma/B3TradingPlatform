# RFC: CVM 505 fund-classification field

| Field    | Value                                                                      |
| -------- | -------------------------------------------------------------------------- |
| Status   | Proposed                                                                   |
| Tracking | [#452](https://github.com/pedrosakuma/B3TradingPlatform/issues/452)        |
| Refs     | [#308](https://github.com/pedrosakuma/B3TradingPlatform/issues/308) (RFC CVM), [#301](https://github.com/pedrosakuma/B3TradingPlatform/issues/301) (sub-accounts), [#439](https://github.com/pedrosakuma/B3TradingPlatform/issues/439) (compliance TODO audit) |

## 1. Context

The on-demand CVM transaction-report exporter (Q4.8 / #308) emits two
report flavours from the same WAL fill stream:

- **CVM 35** — equities transaction report.
- **CVM 505** — *fund* transaction report.

Both serialise through `CvmReportWriter.WriteTransaction`
(`backend/src/B3.Trading.Application/Reports/Cvm/CvmReportWriter.cs`).
The 505 flavour is supposed to carry a `<Fund>` element identifying the
fund the fill belongs to. Today that element is emitted **empty**:

```csharp
// CvmReportWriter.cs:160
if (reportType == CvmReportType.Cvm505)
    w.WriteElementString("Fund", Namespace, string.Empty);
```

with a `TODO(#308)` placeholder, documented as optional in the XSD
(`Schemas/CvmReport.xsd:13`). Two gaps follow:

1. **No classifier.** There is no field anywhere in the pipeline that
   says "this fill belongs to fund X (CNPJ / internal code)", so the
   element can only be empty.
2. **No filtering.** `CvmReportSource.MaterializeRowsAsync` yields
   *every* fill for the `(firmId, date)` pair regardless of report
   type, so a 505 report currently contains non-fund fills too.

This RFC decides **where** the fund classifier lives and **how** it
reaches the writer, so the `<Fund>` element carries a real value and a
505 report is pre-filtered to fund-tagged fills. It is RFC-first
because the change is a source-of-truth decision touching domain, REST,
persistence, and the compliance audit surface (per `AGENTS.md`).

## 2. The existing analogue: `SubAccountId`

The platform already threads one optional, registry-validated
identifier from REST submit all the way to the CVM writer. Fund
classification should not invent a new shape; it should mirror this one
where the semantics match, and *diverge only where they genuinely
differ* (see §4).

`SubAccountId` data path:

| Stage | Location |
| --- | --- |
| REST DTO | `SubmitOrderRequest.SubAccountId` (`OrdersEndpoints.cs:316`) |
| Validation + firm-scoped registry check | `OrdersEndpoints.cs:98-119` against `SubAccountsRegistry` |
| Application command | `OrderSubmissionRequest(..., SubAccountId:)` |
| WAL event (durable) | `OrderSubmittedEvent.SubAccountId` — JSON-optional, legacy ⇒ `null` (`WalEvents.cs:159`) |
| Report read | `CvmReportSource` reads `submit.SubAccountId` (`CvmReportSource.cs:184`) |
| Emission | `<SubAccount>` element, `minOccurs=0` (`CvmReportWriter.cs:150`) |

The registry entry itself is a small, snapshot-persisted record:

```csharp
// SubAccountsRegistry.cs:32
public sealed record Entry(string FirmId, string Id, string? DisplayName, bool Active);
```

## 3. The core question

> Is "is a fund / which fund" a property of **each order**, or of the
> **account that placed it**?

This is the decision the issue defers to compliance. The two
candidate homes:

- **(A) Order-level** — add a fund field to `OrderSubmissionRequest`
  and `OrderSubmittedEvent`, exactly mirroring `SubAccountId`. The
  trader/bot tags every order.
- **(B) Account-level** — the classifier is an attribute of the
  end-client / sub-account in its registry; the report resolves it by
  the fill's owner at materialisation time. The submit path is
  untouched.

### 3.1 Why fund-ness is account-level

CVM 505 is, by regulatory definition, the report of the transactions
of **a fund**. A fund is an *entity* (identified by CNPJ) that holds an
account; it does not become a fund per-order. A trader does not decide
"this order is a fund order and the next one isn't" — every order from
a fund account is a fund transaction, and no order from a non-fund
account ever is. Encoding the classifier per-order (option A) would:

- pollute the hot submit path and every `OrderSubmittedEvent` on the
  WAL with a field that is a pure function of the owner;
- create the possibility of *inconsistent* tagging (two orders from the
  same fund account tagged differently) that the 505 report would then
  have to reconcile or reject;
- still require an account-level source of truth to validate the
  per-order tag against — so it does not remove the registry work, it
  adds to it.

Option B matches the regulatory semantic, keeps the submit path and WAL
order events unchanged (**no order-event migration**), and makes the
505 filter a clean predicate: *owner account is fund-classified*.

## 4. Proposal (recommended: Option B — account-level)

### 4.1 Domain: a `FundClassification` value object

A new immutable value object in `B3.Trading.Domain`, validated in its
constructor like `SubAccountId` / `EndClientId`:

```csharp
// B3.Trading.Domain/FundClassification.cs
public sealed record FundClassification(string Cnpj, string? InternalCode)
{
    // Cnpj: 14 numeric digits (validated, check-digit optional in v0);
    // InternalCode: optional house code for funds without a CNPJ on
    // file yet. At least one identifier must be present.
}
```

The emitted `<Fund>` value is the CNPJ when present, else the internal
code (decision pinned in §6 open question O1).

### 4.2 Registry: carry the classifier on the account entry

Extend the snapshot-persisted registry entry with an optional
classifier. Two sub-options depending on where compliance says a fund
account is registered:

- **B-sub**: on `SubAccountsRegistry.Entry` —
  `Entry(FirmId, Id, DisplayName, Active, FundClassification? Fund)`.
  Natural when funds are modelled as sub-accounts under a firm.
- **B-ec**: on the end-client registry, when a *whole end-client* is a
  fund (no sub-account split).

Both are additive, snapshot-persisted, and backward-compatible (legacy
snapshots deserialise the new field as `null` = "not a fund"). The
recommendation is to support the classifier on **whichever registry
already owns the fund identity**; concretely we expect **B-sub** to be
the common case (a firm trades several funds as sub-accounts) and
**B-ec** as the fallback for single-fund end-clients. The report
resolver (§4.4) checks sub-account first, then end-client.

### 4.3 REST: manage classification on the registry, not on the order

No change to `SubmitOrderRequest`. Instead, the existing sub-account /
end-client admin endpoints gain the optional classifier:

- `POST /sub-accounts` (and the end-client equivalent) accept an
  optional `fund: { cnpj, internalCode }` block.
- `GET` surfaces it so an operator can audit which accounts are
  fund-classified.

This keeps the order hot path and its JSON contract **unchanged**.

### 4.4 Report: resolve + filter at materialisation

`CvmReportSource.MaterializeRowsAsync` already resolves each fill's
`(firmId, owner, subAccountId)`. Add:

1. A `FundClassification?` lookup keyed by that triple against the
   registry (sub-account first, then end-client).
2. Carry the result on a new `CvmFillRow.Fund` field (nullable).
3. When `reportType == Cvm505`, **skip** rows whose `Fund` is `null`
   (non-fund fills are not 505 transactions). CVM 35 is unaffected.

`CvmReportWriter` then emits `<Fund>` with `row.Fund` value for 505,
keeping `minOccurs=0` in the XSD for retro-compatibility with already
generated 35 reports.

### 4.5 Historical reproducibility — the one real caveat

Reports reconstruct from the durable WAL "minutes or years" after the
trading day (`CvmReportSource` class doc). With option B the classifier
is read from **current** registry state, not the state as-of-trade-date.
For a fund's CNPJ this is almost always fine — a fund's CNPJ is stable
and a fund does not retroactively stop being that fund. The risk is
**reclassification** (an account flipped fund↔non-fund, or CNPJ
corrected) changing a historical report's contents.

Mitigation options, in increasing cost:

- **v0 (recommended):** resolve against current registry state and
  document the as-of caveat. The registry is already snapshot+WAL
  backed, so the *current* value is durable across restarts.
- **v1 (deferred):** make the registry classifier change an
  append-only, timestamped WAL event so the resolver can pick the value
  effective on the trade date. Tracked as a follow-up, out of scope
  here.

This caveat is the price of *not* migrating every order event; §3.1
argues it is the right trade.

## 5. Rejected / deferred alternatives

- **Option A (order-level field).** Rejected as the primary design for
  the reasons in §3.1 (semantic mismatch, hot-path/WAL pollution,
  tagging-inconsistency risk). It remains the fallback *iff* compliance
  asserts fund-ness can legitimately vary per order within one account
  — which we do not currently believe.
- **Derive fund from CNPJ pattern on the owner id.** Rejected: owner
  ids are opaque end-client strings, not CNPJs, and the report owner is
  LGPD-opacified before emission (`OpaqueOwner`). No reliable signal.

## 6. Open questions for compliance

- **O1.** `<Fund>` value precedence — CNPJ vs internal house code when
  both exist? (Proposed: CNPJ wins; internal code only when no CNPJ.)
- **O2.** Is a fund ever modelled as a whole end-client (B-ec) in
  practice, or always a sub-account (B-sub)? Determines which registry
  must carry the field for v0.
- **O3.** Is as-of-trade-date classification a real regulatory
  requirement (forcing §4.5 v1 now), or is current-state acceptable for
  v0?
- **O4.** Should a 505 export over a firm with **zero** fund-classified
  accounts return an empty (valid) report, or a 4xx telling the
  operator no funds are configured? (Proposed: empty valid report.)

## 7. Acceptance criteria (when implemented)

- [ ] `FundClassification` value object in `B3.Trading.Domain` with
      constructor validation + unit tests.
- [ ] Optional classifier on the chosen registry entry + snapshot,
      backward-compatible (legacy snapshot ⇒ `null`).
- [ ] Admin REST to set/clear/read the classifier; submit DTO
      unchanged.
- [ ] `CvmReportSource` resolves the classifier per fill, carries it on
      `CvmFillRow`, and pre-filters `type==505` to fund-tagged fills.
- [ ] `CvmReportWriter` emits a populated `<Fund>` for 505; `<Fund>`
      stays `minOccurs=0` so existing 35 reports still validate.
- [ ] Tests: 505 report includes only fund fills with the right
      classifier; 35 report unchanged; legacy snapshot replay yields
      no funds (empty `<Fund>` never emitted for 505 once filtered).
- [ ] Audit propagation: classifier changes flow through the
      accept/reject audit trail (per issue #452 acceptance list).
