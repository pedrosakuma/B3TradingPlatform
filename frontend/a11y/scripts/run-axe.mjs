// Runs axe-core against the static frontend served on http://127.0.0.1:8088/.
// Replaces @axe-core/cli because the latter doesn't expose Chrome launch
// flags, and CI (running as root on ubuntu-latest) needs --no-sandbox.
// Exits non-zero when any WCAG 2.1 AA violation is found.
import puppeteer from "puppeteer";
import { AxePuppeteer } from "@axe-core/puppeteer";

const URL = process.env.TARGET_URL ?? "http://127.0.0.1:8088/";
const TAGS = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

const browser = await puppeteer.launch({
  headless: "shell",
  args: [
    "--no-sandbox",
    "--disable-setuid-sandbox",
    "--disable-dev-shm-usage",
  ],
});

let exitCode = 0;
try {
  const page = await browser.newPage();
  await page.goto(URL, { waitUntil: "networkidle0", timeout: 30_000 });
  const results = await new AxePuppeteer(page).withTags(TAGS).analyze();
  const { violations, passes, incomplete } = results;
  console.log(
    `axe-core ${results.testEngine.version} on ${URL}: ` +
      `${passes.length} passes, ${incomplete.length} incomplete, ${violations.length} violations`,
  );
  if (violations.length > 0) {
    for (const v of violations) {
      console.log(`\n[${v.impact}] ${v.id} — ${v.help}`);
      console.log(`  ${v.helpUrl}`);
      for (const node of v.nodes) {
        console.log(`  - ${node.target.join(" ")}`);
        if (node.failureSummary) {
          console.log(
            `    ${node.failureSummary.replace(/\n/g, "\n    ").trim()}`,
          );
        }
      }
    }
    exitCode = 1;
  }
} catch (err) {
  console.error("axe run failed:", err);
  exitCode = 1;
} finally {
  await browser.close();
}
process.exit(exitCode);
