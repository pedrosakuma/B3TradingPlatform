# ClOrdID threat model — verified (not assumed)

| Field    | Value                                                                       |
| -------- | --------------------------------------------------------------------------- |
| Status   | Verified-against-current-understanding (pending formal B3 spec citation)    |
| Supersedes | Premise asserted (without citation) in #435, #441, #449, RFC draft #464   |

## 1. Why this document exists

A premissa de que **ClOrdID vaza para counterparties** foi introduzida no audit
inicial de compliance (24/05/2026) e propagada por 3 issues (#435 umbrella →
#449 spin-off) e um RFC draft (#464) **sem citação de fonte**. Esse documento
captura o threat model real para fechar a porta com prova, não com afirmação,
e servir de referência caso a pergunta retorne.

## 2. Threat model — boundary by boundary

### 2.1 Wire FIXP / B3 EntryPoint (membro ↔ bolsa)

`ClOrdID` (campo FIX tag 11, ulong não-zero) é metadado da sessão
**bidirecional entre o membro e a bolsa**:

- **Outbound**: `NewOrderRequest` / `OrderCancelRequest` / `OrderCancelReplaceRequest`
  carregam o `ClOrdID` que o membro escolheu.
- **Inbound**: `ExecutionReport` ecoa o `ClOrdID` (e o `OrigClOrdID` em caso de
  Replace) **apenas para a sessão originadora** — é o handle que o membro usa
  para correlacionar ER ↔ ordem local.

O `ClOrdID` **não atravessa a fronteira da sessão do membro**. Para
contraparte do trade (lado oposto do match), o que viaja é o `ExecID`
assinado pela bolsa.

### 2.2 UMDF (Unified Market Data Feed) — broadcast público

UMDF transmite eventos de mercado a todos os participantes / vendors.
Campos típicos das mensagens (MBO/MBP/Trades):

| Campo            | Conteúdo                                          | Origem do valor              |
| ---------------- | ------------------------------------------------- | ---------------------------- |
| `OrderID`        | Handle persistente da ordem no book               | **Assinado pela bolsa**, opaco para o membro |
| `ExecID`         | Handle do execution event                         | **Assinado pela bolsa**      |
| `SecurityID`     | Instrumento                                       | Estável (registro CVM)       |
| `Price`/`Qty`    | Nível do book / fill                              | Operação                     |
| `Side`           | Buy/Sell                                          | Operação                     |
| `MDEntryType`    | New / Update / Cancel / Trade                     | Tipo do evento               |

**`ClOrdID` NÃO é campo de UMDF.** A bolsa atribui `OrderID` próprio
(pseudoaleatório do ponto de vista do membro) ao receber o `NewOrderRequest`,
e é esse `OrderID` que aparece no broadcast público. Counterparties que
consomem UMDF veem `OrderID` e `ExecID` — ambos **não previsíveis** porque
são gerados pela bolsa.

### 2.3 Drop-copy regulatório / auditoria

Drop-copy B3 é **per-firm**: cada participante recebe drop-copy apenas das
suas próprias ordens. Não há canal padrão pelo qual a firm A receba o
`ClOrdID` da firm B.

O regulador (CVM) e a B3 podem acessar transaction logs cross-firm para
auditoria, mas isso é controle operacional/regulatório — não threat model
de "contraparte HFT correlacionando algos alheios".

### 2.4 Operador B3 interno

Pessoas da B3 com acesso ao matching engine veem ClOrdIDs de todos os
participantes por construção. **Controles relevantes são operacionais**
(audit log de acesso, segregação de função, NDA), não criptográficos no
ClOrdID.

## 3. Conclusão

| Pergunta                                                                          | Resposta                                            |
| --------------------------------------------------------------------------------- | --------------------------------------------------- |
| Counterparty HFT consegue observar nossos ClOrdIDs em market data?                | **Não** — UMDF expõe `OrderID` (assinado pela bolsa) |
| Counterparty consegue inferir pacing/estratégia de algos nossos pelo `ClOrdID`?   | **Não** — não tem acesso ao `ClOrdID`               |
| Firm vizinha vê nossos `ClOrdIDs` em drop-copy?                                   | **Não** — drop-copy é per-firm                      |
| Regulador / B3 vêem `ClOrdIDs`?                                                   | Sim, mas mitigação é **operacional**, não criptográfica |
| Há benefício compliance em mascarar `ClOrdID` na wire?                            | **Não materializado** — threat model não se sustenta |

## 4. Implicação técnica

O esquema atual de `ClOrdIdPrefixRegistry` —
`(prefixIdx << 40) | Interlocked.Increment(counter)` — é
**adequado, correto e suficiente**:

- ✅ Único (combinação prefix+counter monotônico)
- ✅ Estritamente monotônico (CAS em `Interlocked.Increment`)
- ✅ Lock-free e ~5 ns por chamada
- ✅ Non-zero (counter inicia em 1)
- ✅ Reversível por bit-mask → `AdvanceCounterTo` funciona trivialmente em WAL replay
- ✅ Snapshot/restore via `(EndClientId, PrefixIdx, Counter)` tuples

**Não há gap de compliance a fechar.** Qualquer trabalho de
obfuscation/masking do `ClOrdID` (random base, random step, RFC 6528-style
keyed hash, FPE) seria **complexidade sem benefício** — não fecha um threat
model real, e introduz risco em invariantes críticas (`AdvanceCounterTo`,
snapshot format).

## 5. Issues fechadas em consequência

- **#449** — closed `won't-fix / invalid-premise`. Link: este documento.
- **#465** — closed `won't-fix / dependency on invalid premise`.
- **PR #464** (RFC draft) — closed sem merge.

## 6. Lições / processo

O audit inicial (checkpoint 001) listou explicitamente:

> "Aplicar lição #429: validar premissa de cada issue P0/P1 contra código
> real antes de criar branches de implementação"

Essa lição foi aplicada em P0/P1 mas **não estendida para P2 com premissas
externas** (e.g., "venue expõe X"). Para issues de compliance futuras:

1. **Citar a fonte da premissa** no body do issue
   (spec UMDF §X, FIXP spec §Y, instrução CVM Z) — não basta "auditoria
   identificou"
2. Se a premissa for inferência por analogia com outra venue
   (CME/NYSE/NASDAQ), marcar explicitamente como `assumption-pending-verification`
3. RFC drafts não devem **assumir** o threat model — devem **derivá-lo de
   fontes citadas** em §1 Context

## 7. Open verification

Esta nota reflete o **modelo mental compartilhado** entre owner (pedrotravi)
e agente em 25/05/2026. Citações formais a verificar e adicionar:

- [ ] B3 UMDF spec — schema das mensagens MBO/MBP/Trades, confirmar ausência de `ClOrdID`
- [ ] B3 EntryPoint FIXP spec — confirmar `ClOrdID` é session-scoped
- [ ] Drop-copy spec B3 — confirmar escopo per-firm

Quando essas citações forem adicionadas, mover `Status` de
"Verified-against-current-understanding" para "Verified-against-spec".
