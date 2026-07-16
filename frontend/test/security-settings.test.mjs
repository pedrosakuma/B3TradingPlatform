import test from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

test("Security settings render exactly the action allowed by status", async () => {
  const { elements } = installDomStub({
    ids: {
      "security-status": {},
      "security-enroll-start": { hidden: true },
      "security-enroll-show": { hidden: true },
      "security-enrolled": { hidden: true },
    },
  });
  const settingsUi = await import("../js/settingsUi.js");

  settingsUi.renderSecurityPanelState({ status: "not-enrolled" });
  assert.equal(elements.get("security-enroll-start").hidden, false);
  assert.equal(elements.get("security-enroll-show").hidden, true);
  assert.equal(elements.get("security-enrolled").hidden, true);

  settingsUi.renderSecurityPanelState({ status: "pending" });
  assert.equal(elements.get("security-enroll-start").hidden, true);
  assert.equal(elements.get("security-enroll-show").hidden, false);
  assert.equal(elements.get("security-enrolled").hidden, true);

  settingsUi.renderSecurityPanelState({ status: "enrolled" });
  assert.equal(elements.get("security-enroll-start").hidden, true);
  assert.equal(elements.get("security-enroll-show").hidden, true);
  assert.equal(elements.get("security-enrolled").hidden, false);
});
