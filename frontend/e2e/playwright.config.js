// Playwright config for the opt-in frontend smoke E2E.
// Triggered via .github/workflows/e2e-smoke.yml (workflow_dispatch),
// not on every PR. Run locally with:
//   docker compose -f docker/docker-compose.yml -f docker/docker-compose.e2e.yml up -d --build
//   cd frontend/e2e && npm install && npx playwright install --with-deps chromium && npm test

import { defineConfig, devices } from "@playwright/test";

const FRONTEND_URL = process.env.E2E_FRONTEND_URL ?? "http://localhost:8080";

export default defineConfig({
  testDir: ".",
  testMatch: /.*\.spec\.js$/,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  retries: 0,
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: FRONTEND_URL,
    headless: true,
    trace: "retain-on-failure",
    video: "retain-on-failure",
    ignoreHTTPSErrors: true,
  },
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
  ],
});
