# Frontend a11y + perf gate

A11y (axe-core) + perf (Lighthouse CI) gate for the static frontend.
Runs on every PR via `.github/workflows/ci.yml` (job: `Frontend a11y`).

## What it does

1. Serves `frontend/` via `http-server` on `127.0.0.1:8088`.
2. Runs **`@axe-core/cli`** against the loaded shell, asserting **zero
   `wcag2a` / `wcag2aa` / `wcag21a` / `wcag21aa` violations**.
3. Runs **Lighthouse CI** (`@lhci/cli autorun`) with assertions:
   - **accessibility ≥ 0.90** (hard error)
   - **performance ≥ 0.70**  (warning — desktop preset)
   - **best-practices ≥ 0.85** (warning)

## Scope today

The audit walks the **login screen** only. With hash routing
(`/#trader`, `/#algos`, `/#settings`) the SPA bounces unauthenticated
sessions back to login, so auditing extra routes today would just
re-audit the same DOM.

Auditing authenticated routes requires either a backend mock or a
seeded JWT injected into `localStorage` before the audit run — tracked
as a follow-up.

## Running locally

```sh
cd frontend/a11y
npm install
# Terminal A: serve the static frontend
npm run serve
# Terminal B: run the gate
npm run axe   # requires a system Chrome on $PATH
npm run lhci
```

`@axe-core/cli` shells out to `selenium-webdriver` and looks for a
system Chrome binary (`google-chrome` / `chromium`). The CI runner
(ubuntu-latest) has it preinstalled at `/usr/bin/google-chrome`.

Lighthouse CI bundles Chromium via Puppeteer — no system browser
required.

## Pinned deps

| Package          | Version  | Why                              |
| ---------------- | -------- | -------------------------------- |
| `@axe-core/cli`  | `4.10.0` | axe-core 4.10.x reference impl   |
| `@lhci/cli`      | `0.14.0` | Lighthouse 12.x                  |
| `http-server`    | `14.1.1` | Static server with no-cache mode |

Bumps to any of the three should land alongside a fresh
`package-lock.json` so the CI cache key invalidates correctly.

## Tuning the thresholds

Edit `lighthouserc.json`. The desktop preset already eliminates the
mobile throttling that historically tanked perf scores for full-fat
trader UIs; the 0.70 floor is intentionally loose because the gate is
defensive (catches regressions), not prescriptive.
