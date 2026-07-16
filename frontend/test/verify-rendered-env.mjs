import fs from "node:fs";
import vm from "node:vm";

const source = fs.readFileSync(process.argv[2], "utf8");
const context = { window: {} };
vm.runInNewContext(source, context, { filename: process.argv[2] });
const config = context.window.__B3_CONFIG__;

function assertB64(name, actual, expectedB64) {
  const actualB64 = Buffer.from(actual ?? "", "utf8").toString("base64");
  if (actualB64 !== expectedB64) {
    throw new Error(`${name} mismatch: ${JSON.stringify(actual)} (${actualB64}) != ${expectedB64}`);
  }
}

assertB64("appTitle", config?.appTitle, process.env.EXPECTED_APP_TITLE_B64);
assertB64("marketDataWsUrl", config?.marketDataWsUrl, process.env.EXPECTED_MARKETDATA_WS_URL_B64);
assertB64("auth.authority", config?.auth?.authority, process.env.EXPECTED_AUTH_AUTHORITY_B64);
assertB64("auth.clientId", config?.auth?.clientId, process.env.EXPECTED_AUTH_CLIENT_ID_B64);
assertB64("auth.apiScope", config?.auth?.apiScope, process.env.EXPECTED_AUTH_API_SCOPE_B64);
if (!Array.isArray(config?.auth?.knownAuthorities) || config.auth.knownAuthorities.length !== 2) {
  throw new Error("auth.knownAuthorities shape mismatch");
}
assertB64("auth.knownAuthorities[1]", config.auth.knownAuthorities[1], process.env.EXPECTED_AUTH_KNOWN_AUTHORITY_B64);
