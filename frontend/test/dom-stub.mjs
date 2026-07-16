// Minimal DOM stub for node:test runs of the credentials UI module.
//
// We intentionally avoid pulling in jsdom — the rest of frontend/test/
// uses pure node:test against plain functions, and the CI step only
// runs `node --test` with no install step. This stub mimics just the
// subset of the DOM surface botCredentialsUi.js touches: getElementById,
// addEventListener (no-op), focus()/select() (no-op), classList,
// dataset, .innerHTML / .textContent / .value / .hidden / .className,
// requestAnimationFrame (sync), and document.execCommand (no-op).

class FakeClassList {
  constructor() { this._set = new Set(); }
  add(...c) { for (const x of c) this._set.add(x); }
  remove(...c) { for (const x of c) this._set.delete(x); }
  toggle(c, on) {
    if (on === undefined) on = !this._set.has(c);
    if (on) this._set.add(c); else this._set.delete(c);
  }
  contains(c) { return this._set.has(c); }
  toString() { return [...this._set].join(" "); }
}

class FakeElement {
  constructor(tag) {
    this.tagName = String(tag || "div").toUpperCase();
    this._listeners = new Map();
    this._dataset = {};
    this._attributes = new Map();
    this.classList = new FakeClassList();
    this.hidden = false;
    this.disabled = false;
    this.value = "";
    this.textContent = "";
    this.innerHTML = "";
    this.className = "";
    this.children = [];
  }
  get dataset() { return this._dataset; }
  addEventListener(type, fn) {
    if (!this._listeners.has(type)) this._listeners.set(type, []);
    this._listeners.get(type).push(fn);
  }
  removeEventListener(type, fn) {
    const l = this._listeners.get(type);
    if (!l) return;
    const i = l.indexOf(fn);
    if (i >= 0) l.splice(i, 1);
  }
  dispatchEvent(event) {
    const e = event ?? {};
    const type = typeof e.type === "string" ? e.type : "";
    if (!("target" in e) || e.target == null) {
      try { Object.defineProperty(e, "target", { value: this, configurable: true }); } catch {}
    }
    if (typeof e.preventDefault !== "function") {
      try { Object.defineProperty(e, "preventDefault", { value: () => {}, configurable: true }); } catch {}
    }
    for (const fn of this._listeners.get(type) ?? []) fn(e);
    return true;
  }
  setAttribute(name, value) { this._attributes.set(String(name), String(value)); }
  removeAttribute(name)     { this._attributes.delete(String(name)); }
  getAttribute(name)        { return this._attributes.has(String(name)) ? this._attributes.get(String(name)) : null; }
  hasAttribute(name)        { return this._attributes.has(String(name)); }
  focus() {}
  select() {}
  closest() { return null; }
  querySelector() { return null; }
  querySelectorAll() { return []; }
  appendChild(c) { this.children.push(c); return c; }
}

export function installDomStub({ ids = {} } = {}) {
  const elements = new Map();
  for (const [id, spec] of Object.entries(ids)) {
    const el = new FakeElement(spec?.tag);
    if (spec?.hidden !== undefined) el.hidden = !!spec.hidden;
    elements.set(id, el);
  }

  const documentStub = {
    getElementById: (id) => elements.get(id) ?? null,
    querySelector: () => null,
    querySelectorAll: () => [],
    addEventListener: () => {},
    removeEventListener: () => {},
    execCommand: () => false,
  };

  globalThis.document = documentStub;
  globalThis.window = globalThis.window ?? {
    confirm: () => true,
  };
  globalThis.requestAnimationFrame =
    globalThis.requestAnimationFrame ?? ((fn) => { try { fn(0); } catch { /* ignore */ } return 0; });
  globalThis.navigator = globalThis.navigator ?? {};

  return { elements };
}
