// Fase 2 (#398). Algos tab — list / boleta / detail.
//
// Render-only module that mirrors the ui.js pattern: a single `render()`
// function reads `state.algos` and rebuilds the panel; gesture handlers
// (submit / cancel / modify / select) call back into app.js via the
// `actions` registered at init time.

import {
  getState, subscribe, isTerminalAlgoStatus,
  setSelectedAlgoId, setAlgosFilter,
} from "./state.js";
import { fmtQty, fmtPx } from "./ui.js";

const $ = (id) => document.getElementById(id);

let _actions = {
  onSubmitAlgo: () => {},
  onCancelAlgo: () => {},
  onModifyAlgo: () => {},
};

// Snapshot of the currently-selected algo type — drives which sub-form
// is visible. Persisted to localStorage so the boleta survives reloads.
let _selectedType = (typeof localStorage !== "undefined"
  && localStorage.getItem("algos.lastType")) || "Iceberg";

function _setType(t) {
  _selectedType = t;
  try { localStorage?.setItem("algos.lastType", t); } catch { /* no-op */ }
}

// ── Render --------------------------------------------------------

function renderList() {
  const tbody = $("algos-list-body");
  if (!tbody) return;
  const st = getState();
  const { text, hideTerminal } = st.algosFilter;
  const q = (text || "").toLowerCase();
  const rows = [...st.algos.values()].filter(a => {
    if (hideTerminal && isTerminalAlgoStatus(a.status)) return false;
    if (q) {
      const hay = `${a.algoId} ${a.symbol} ${a.type} ${a.status}`.toLowerCase();
      if (!hay.includes(q)) return false;
    }
    return true;
  }).sort((a, b) => {
    // Newest first by createdAt (server timestamps are ISO).
    const ta = Date.parse(a.createdAtUtc) || 0;
    const tb = Date.parse(b.createdAtUtc) || 0;
    return tb - ta;
  });

  tbody.replaceChildren();
  if (rows.length === 0) {
    const tr = document.createElement("tr");
    const td = document.createElement("td");
    td.colSpan = 7;
    td.className = "muted-line";
    td.textContent = "No algos at the moment.";
    tr.appendChild(td);
    tbody.appendChild(tr);
    return;
  }
  for (const a of rows) {
    const tr = document.createElement("tr");
    tr.dataset.algoId = a.algoId;
    if (a.algoId === st.selectedAlgoId) tr.classList.add("selected");
    const cells = [
      a.algoId,
      a.symbol,
      a.side,
      a.type,
      `${fmtQty(a.filledQuantity)} / ${fmtQty(a.totalQuantity)}`,
      a.status,
      a.terminalReason && a.terminalReason !== "None" ? a.terminalReason : "",
    ];
    for (const c of cells) {
      const td = document.createElement("td");
      td.textContent = c;
      tr.appendChild(td);
    }
    tr.addEventListener("click", () => setSelectedAlgoId(a.algoId));
    tbody.appendChild(tr);
  }
}

function renderDetail() {
  const panel = $("algos-detail-body");
  if (!panel) return;
  const st = getState();
  const id = st.selectedAlgoId;
  const a = id ? st.algos.get(id) : null;
  panel.replaceChildren();
  if (!a) {
    const p = document.createElement("p");
    p.className = "muted-line";
    p.textContent = "Select an algo from the list.";
    panel.appendChild(p);
    return;
  }
  const terminal = isTerminalAlgoStatus(a.status);
  const lines = [
    ["AlgoId", a.algoId],
    ["Symbol", a.symbol],
    ["Side", a.side],
    ["Type", a.type],
    ["Total / Filled / Remaining", `${fmtQty(a.totalQuantity)} / ${fmtQty(a.filledQuantity)} / ${fmtQty(a.remainingQuantity)}`],
    ["Status", a.status],
    ["TerminalReason", a.terminalReason],
    ["CreatedAtUtc", a.createdAtUtc],
    ["TerminalAtUtc", a.terminalAtUtc || "—"],
  ];
  const dl = document.createElement("dl");
  dl.className = "algos-detail-grid";
  for (const [k, v] of lines) {
    const dt = document.createElement("dt"); dt.textContent = k;
    const dd = document.createElement("dd"); dd.textContent = String(v);
    dl.appendChild(dt); dl.appendChild(dd);
  }
  panel.appendChild(dl);

  // Per-type params summary (best-effort — undefined blocks are skipped).
  const paramsBlock = a.iceberg || a.twap || a.vwap || a.pov || a.pegged;
  if (paramsBlock) {
    const h4 = document.createElement("h4"); h4.textContent = "Parameters";
    const pre = document.createElement("pre"); pre.className = "algos-params-pre";
    pre.textContent = JSON.stringify(paramsBlock, null, 2);
    panel.appendChild(h4); panel.appendChild(pre);
  }

  // Modify form (qty/price) — disabled when terminal/cancelling.
  const form = document.createElement("form");
  form.className = "algos-modify-form";
  form.innerHTML = `
    <h4>Modify</h4>
    <label>New quantity <input type="number" name="newQuantity" min="1" step="1"></label>
    <label>New price <input type="number" name="newPrice" step="0.01"></label>
    <button type="submit" class="primary">Modify</button>
    <p class="muted-line">Provide at least one of the two fields.</p>
  `;
  const cancelling = a.status === "Cancelling";
  const inflightMod = st.inflightAlgoModifies.has(a.algoId);
  const inflightCxl = st.inflightAlgoCancels.has(a.algoId);
  for (const inp of form.querySelectorAll("input,button")) inp.disabled = terminal || cancelling || inflightMod;
  form.addEventListener("submit", (ev) => {
    ev.preventDefault();
    const fd = new FormData(form);
    const newQuantity = fd.get("newQuantity");
    const newPrice = fd.get("newPrice");
    const payload = {};
    if (newQuantity !== "" && newQuantity != null) payload.newQuantity = Number(newQuantity);
    if (newPrice !== "" && newPrice != null) payload.newPrice = Number(newPrice);
    if (payload.newQuantity == null && payload.newPrice == null) {
      _setStatus("Provide newQuantity and/or newPrice", "error");
      return;
    }
    _actions.onModifyAlgo(a.algoId, payload);
  });
  panel.appendChild(form);

  const cancelBtn = document.createElement("button");
  cancelBtn.type = "button";
  cancelBtn.className = "danger";
  cancelBtn.textContent = inflightCxl ? "Cancelling…" : "Cancel algo";
  cancelBtn.disabled = terminal || inflightCxl;
  cancelBtn.addEventListener("click", () => _actions.onCancelAlgo(a.algoId));
  panel.appendChild(cancelBtn);
}

function renderBoleta() {
  const tabs = document.querySelectorAll("#algos-boleta .algos-type-tab");
  tabs.forEach(t => {
    t.classList.toggle("active", t.dataset.algoType === _selectedType);
    t.setAttribute("aria-selected", t.dataset.algoType === _selectedType ? "true" : "false");
  });
  document.querySelectorAll("#algos-boleta .algos-subform").forEach(f => {
    f.hidden = f.dataset.algoType !== _selectedType;
  });
}

function _setStatus(msg, kind) {
  const el = $("algos-boleta-status");
  if (!el) return;
  el.textContent = msg || "";
  el.className = "algos-boleta-status" + (kind ? ` ${kind}` : "");
}

export function showBoletaError(msg) { _setStatus(msg, "error"); }
export function showBoletaSuccess(msg) { _setStatus(msg, "success"); }

function _readBoletaPayload() {
  const form = $("algos-boleta-form");
  if (!form) return null;
  const fd = new FormData(form);
  const payload = {
    symbol: String(fd.get("symbol") || "").trim().toUpperCase(),
    securityId: Number(fd.get("securityId") || 0),
    side: String(fd.get("side") || "Buy"),
    type: _selectedType,
    totalQuantity: Number(fd.get("totalQuantity") || 0),
  };
  const num = (v) => (v === "" || v == null ? null : Number(v));
  switch (_selectedType) {
    case "Iceberg":
      payload.iceberg = {
        displayQuantity: Number(fd.get("ice.displayQuantity") || 0),
        limitPrice: num(fd.get("ice.limitPrice")),
      };
      break;
    case "Twap":
      payload.twap = {
        startUtc: String(fd.get("twap.startUtc") || ""),
        endUtc: String(fd.get("twap.endUtc") || ""),
        sliceCount: Number(fd.get("twap.sliceCount") || 0),
        childOrderType: String(fd.get("twap.childOrderType") || "Limit"),
        childPrice: num(fd.get("twap.childPrice")),
      };
      break;
    case "Vwap":
      payload.vwap = {
        startUtc: String(fd.get("vwap.startUtc") || ""),
        endUtc: String(fd.get("vwap.endUtc") || ""),
        childOrderType: String(fd.get("vwap.childOrderType") || "Limit"),
        childPrice: num(fd.get("vwap.childPrice")),
        tickIntervalSeconds: num(fd.get("vwap.tickIntervalSeconds")),
        sliceMaxPct: num(fd.get("vwap.sliceMaxPct")),
        participationCap: num(fd.get("vwap.participationCap")),
        priceLimit: num(fd.get("vwap.priceLimit")),
      };
      break;
    case "Pov":
      payload.pov = {
        startUtc: String(fd.get("pov.startUtc") || ""),
        endUtc: String(fd.get("pov.endUtc") || ""),
        childOrderType: String(fd.get("pov.childOrderType") || "Limit"),
        childPrice: num(fd.get("pov.childPrice")),
        participationRate: Number(fd.get("pov.participationRate") || 0),
        tickIntervalSeconds: num(fd.get("pov.tickIntervalSeconds")),
        minSliceQty: num(fd.get("pov.minSliceQty")),
        priceLimit: num(fd.get("pov.priceLimit")),
      };
      break;
    case "Pegged":
      payload.pegged = {
        ref: String(fd.get("pegged.ref") || "Mid"),
        offsetTicks: Number(fd.get("pegged.offsetTicks") || 0),
        repegIntervalMs: num(fd.get("pegged.repegIntervalMs")),
        tickSize: num(fd.get("pegged.tickSize")),
        priceLimit: num(fd.get("pegged.priceLimit")),
      };
      break;
  }
  return payload;
}

export function readBoletaPayload() { return _readBoletaPayload(); }

// ── Init / wiring ------------------------------------------------

let _initialised = false;
export function initAlgosUi(actions) {
  _actions = { ..._actions, ...(actions || {}) };
  if (_initialised) return;
  _initialised = true;

  // Type tab handlers (boleta).
  document.querySelectorAll("#algos-boleta .algos-type-tab").forEach(tab => {
    tab.addEventListener("click", () => {
      _setType(tab.dataset.algoType);
      renderBoleta();
    });
  });
  // Submit handler.
  const form = $("algos-boleta-form");
  if (form) {
    form.addEventListener("submit", (ev) => {
      ev.preventDefault();
      _setStatus("", null);
      const payload = _readBoletaPayload();
      _actions.onSubmitAlgo(payload);
    });
  }
  // Filter handlers.
  const filterText = $("algos-filter-text");
  if (filterText) filterText.addEventListener("input", (ev) => setAlgosFilter({ text: ev.target.value || "" }));
  const filterHide = $("algos-filter-hide-terminal");
  if (filterHide) filterHide.addEventListener("change", (ev) => setAlgosFilter({ hideTerminal: !!ev.target.checked }));

  // Re-render on any algos slice notification.
  subscribe((slice) => {
    if (slice !== "algos" && slice !== "all") return;
    renderList();
    renderDetail();
  });

  renderBoleta();
  renderList();
  renderDetail();
}

export function renderAlgos() {
  renderList();
  renderDetail();
  renderBoleta();
}
