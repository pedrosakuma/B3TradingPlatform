# Per-firm B3.EntryPoint credentials

Status: shipped via #126.

## Overview

Each firm that the trading host opens a FIXP session for needs an access key the
B3 EntryPoint gateway accepts in `Negotiate.Credentials`. The host supports two
shapes for declaring that key under `Trading:Exchange:Firms[i]`:

1. **Legacy flat field** (`AccessKey`) — kept for back-compat with existing
   deployments. Logs a deprecation WARN at startup.
2. **Structured `Credentials` bundle** (preferred) — discriminated by `Mode`,
   supports file-mounted secret indirection.

Today only `Mode: AccessKey` is wired because B3.EntryPoint.Client 0.14.3 only
exposes `Credentials.FromUtf8(accessKey)`. New SDK modes can be added without
re-shaping deployed configs (`Mode: Certificate`, `Mode: Token`, …).

## Shapes

### Legacy (back-compat)

```json
{
  "Trading": {
    "Exchange": {
      "Mode": "Real",
      "Firms": [
        {
          "FirmId": "FIRM01",
          "AccessKey": "literal-access-key"
        }
      ]
    }
  }
}
```

### Structured — inline (dev)

```json
"Firms": [{
  "FirmId": "FIRM01",
  "Credentials": {
    "Mode": "AccessKey",
    "AccessKey": "literal-access-key"
  }
}]
```

### Structured — file-mounted (production)

```json
"Firms": [{
  "FirmId": "FIRM01",
  "Credentials": {
    "Mode": "AccessKey",
    "AccessKeyFile": "/run/secrets/firm01-access-key"
  }
}]
```

The file is read once at startup, trimmed of surrounding whitespace, and held
in process memory by the SDK. Permission requirements (Linux only):

| Mode | Accepted |
| --- | --- |
| `0600` (rw-------) | ✅ |
| `0400` (r--------) | ✅ |
| anything group- or world-readable | ❌ host refuses to boot |

On non-Linux runners (Windows / macOS dev loops) the permission check is
skipped — those environments rely on filesystem ACLs.

## docker-compose example

```yaml
services:
  trading-host:
    image: b3-trading-host:latest
    environment:
      Trading__Exchange__Mode: Real
      Trading__Exchange__Firms__0__FirmId: FIRM01
      Trading__Exchange__Firms__0__Endpoint: broker.example.com:9000
      Trading__Exchange__Firms__0__SessionId: "100"
      Trading__Exchange__Firms__0__SessionVerId: "1"
      Trading__Exchange__Firms__0__EnteringFirm: "200"
      Trading__Exchange__Firms__0__SenderLocation: BR-SP
      Trading__Exchange__Firms__0__EnteringTrader: TR1
      Trading__Exchange__Firms__0__Credentials__Mode: AccessKey
      Trading__Exchange__Firms__0__Credentials__AccessKeyFile: /run/secrets/firm01_access_key
    secrets:
      - firm01_access_key

secrets:
  firm01_access_key:
    file: ./secrets/firm01_access_key.txt
```

Make sure the host-side file is `chmod 0600` and owned by the user the
container runs as.

## Validation

`ExchangeOptionsValidator` (eager-fail at startup) enforces shape:

- One of `AccessKey` (legacy) **or** `Credentials` must be set.
- For `Credentials.Mode = AccessKey`: exactly one of `AccessKey` or
  `AccessKeyFile` is required.
- An unknown `Mode` is refused (forward-compat guard rail).

The file permission check runs later, in `FirmCredentialResolver`, because it
needs a filesystem stat that options validation deliberately avoids.

## Rotation

Today rotation requires a host restart (the credential is read once at startup
and the SDK holds the bytes for the lifetime of the FIXP session). When this
becomes operationally painful, follow up with an `IOptionsMonitor`-driven
re-read; tracked in a separate ticket (kept out of #126).

## Logging

`FirmConfig` / `FirmCredentialsConfig` never write the secret material to logs.
`FirmCredentialsConfig.ToString()` redacts both the inline value (length only)
and any caller that structured-logs the bundle gets the redacted projection.
Always pair `Trading__Exchange__Firms__0__AccessKey` style env vars with secret
backends (docker secrets, k8s Secret, Vault sidecar) in production.

## Out of scope

The following follow-ups are intentionally deferred:

- Cloud secret managers (AWS SM, Azure Key Vault, HashiCorp Vault) — wire as
  `Mode: VaultRef` when needed.
- Hot-reload / rotation without restart — `IOptionsMonitor` integration.
- mTLS layered under the FIXP gateway — separate from credentials, lives at
  the socket layer.
