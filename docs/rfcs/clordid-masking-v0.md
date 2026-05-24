# RFC: ClOrdID disclosure / masking v0

| Field    | Value                                                                                                       |
| -------- | ----------------------------------------------------------------------------------------------------------- |
| Status   | Proposed                                                                                                    |
| Tracking | [#449](https://github.com/pedrosakuma/B3TradingPlatform/issues/449) (split from [#435](https://github.com/pedrosakuma/B3TradingPlatform/issues/435) Part B) |
| Umbrella | [#441](https://github.com/pedrosakuma/B3TradingPlatform/issues/441) (auditoria compliance B3)               |
| Touches  | `B3.Trading.Application/ClOrdIdPrefixRegistry.cs`, snapshot DTOs, 5 service call sites, 17 tests            |

## 1. Context

`ClOrdIdPrefixRegistry` (Application) é o chokepoint único para alocação de
todo `ulong` ClOrdID que sai da plataforma — submit, replace e cancel
passam pelos 5 call sites em `OrderSubmissionService` /
`OrderCancelService` / `OrderModifyService` / `AlgoEngine` (×2 — child de
algo + repeg). A codificação atual (decidida em #7) é:

```
ClOrdID (ulong) = (prefixIdx << 40) | counter
                  └─ 21 bits ────┘  └ 40 bits ┘
```

- `prefixIdx` é uma sequência global monotônica alocada **por end-client**
  na primeira chamada (`CreateCounter`); cap defensivo em 2²¹ ≈ 2M
  end-clients.
- `counter` é monotônico **por end-client**, avançado com
  `Interlocked.Increment` a partir de 1 (zero nunca é produzido — invariante
  EntryPoint `ClOrdID != 0`).
- Bit 63 sempre zero (fica abaixo de `long.MaxValue`).

Resultado observável na wire: dois ClOrdIDs gerados em sequência por um
mesmo algo diferem por exatamente **+1**. Um algo com 100 children produz
IDs `K, K+1, K+2, …, K+99` no mesmo prefixo.

## 2. Risco compliance

Counterparties que recebem ERs públicos (no caso B3, via canal de book
agressor / fila / negócios) podem observar os ClOrdIDs de orders alheias
em alguns contextos (drop-copy de membro, dados de mercado nível 3,
participação em leilão). Mesmo sem essa observação direta, **o próprio
operador B3 + entidades reguladas que recebem drop-copy** veem o padrão.

Riscos concretos:

1. **CVM Instrução 168 — práticas equitativas** / *information leak*:
   contraparte que infere "estes 30 IDs contíguos no prefixo X são
   children do mesmo algo de Y" ganha sinal estratégico (tamanho restante
   da ordem mãe, ritmo de execução, intenção de continuar).
2. **HFT cross-correlation**: prefixIdx por end-client é estável across
   sessions enquanto o snapshot persistir. Um adversário consegue ligar
   atividade do mesmo cliente em dias diferentes ("ontem o operador
   institucional X enviou ordem da manhã às 10:31 — vejo o mesmo prefixo
   hoje às 09:55, ele está acumulando").
3. **Algo fingerprinting**: padrão de **gaps** entre children
   (incremento +1 contínuo vs +1 com bursts) revela o pacing do algo
   (TWAP regular vs POV reativo vs Pegged com repeg-on-tick).

## 3. Non-goals

- Substituir a invariante de ID único + non-zero por algo probabilístico
  (UUID). EntryPoint exige `ulong`, e a probabilidade de colisão em 2⁶⁴
  é matematicamente irrelevante mas operacionalmente intratável (ER
  routing precisa de igualdade exata, não "quase").
- Esconder ClOrdIDs do nosso próprio storage / observability / audit. O
  WAL, snapshots e métricas continuam vendo IDs limpos — isto é
  *external-facing* hardening, não internal redaction.
- Quebrar a invariante de `AdvanceCounterTo` (WAL replay precisa decidir
  monotonicamente "qual o próximo ID seguro" a partir do maior já
  observado).
- Mascarar o `prefixIdx` no *snapshot* (binding identidade do
  end-client ↔ prefixo é por design — sem ele, restore não consegue
  reconstruir).

## 4. Options

### 4.1 Opção 1 — Per-(end-client × ParentAlgoId) prefix slot

Cada par `(EndClientId, ParentAlgoId)` recebe um `prefixIdx` próprio na
primeira `Generate` daquele algo. Algos diferentes do mesmo cliente
ficam em namespaces de prefixo distintos.

**Pros:**
- Cross-algo correlation eliminada (não dá pra dizer "estes IDs vieram
  do mesmo cliente, são algos diferentes do mesmo operador").
- AdvanceCounterTo continua válido se o snapshot persistir o par
  `(end-client, parentAlgoId) → prefixIdx`.

**Cons:**
- **Esgota 2²¹ prefixos rapidamente** em cenário multi-firm com algos
  frequentes (1000 clientes × 100 algos/dia × 30 dias = 3M — overflow em
  ~1 mês). Precisa migrar `MaxPrefixIndex` para 2³² (e reservar 32 bits
  para counter, divindindo a 1.1T → 4B IDs por slot — suficiente).
- **5 call sites precisam threadar `ParentAlgoId?`** até a registry; o
  cancel/modify desktop (não-algo) cairia no default `(client, null)`
  bucket — comportamento idêntico ao de hoje pra fluxo non-algo, mas
  precisa do parâmetro extra na assinatura.
- **Não resolve** dentro do algo — children continuam `K, K+1, K+2`
  (AC#2 falha: "100 child ClOrdIDs de mesmo algo **não revelam
  sequência contígua**").
- Snapshot format break: `ClOrdIdCounterSnapshot(string EndClientId,
  ulong PrefixIdx, long Counter)` precisa virar
  `(string EndClientId, ulong? ParentAlgoId, ulong PrefixIdx, long
  Counter)`.

### 4.2 Opção 2 — Random `counter` base por sessão

Em vez de iniciar `counter = 0`, sortear `counter₀ ∈ [1, 2³²]` em
`CreateCounter`. `Generate` continua `Interlocked.Increment`. Os IDs
viram `counter₀+1, counter₀+2, …`.

**Pros:**
- **Mudança mínima** — 1 linha em `CreateCounter`.
- AdvanceCounterTo, Snapshot, Restore inalterados (counter é persistido
  como-está; restore reproduz a mesma base).
- Quebra **cross-session correlation**: counterparty não consegue
  predicar "este cliente continuou de onde parou ontem".
- Zero impacto em testes que pinam offsets relativos (`+1`, `next - prev`).

**Cons:**
- **Não resolve a AC#2 dentro do algo**: children ainda `K, K+1, K+2`.
- Apenas mitigação cross-session — within-session a sequência contígua
  continua observável.
- Esgotamento do counter mais cedo: base random em [1, 2³²] consome
  4G entradas do espaço 2⁴⁰ por end-client antes de qualquer Generate
  → ~1.1T – 4G ≈ 1.1T válidas restantes. Operacionalmente trivial.

### 4.3 Opção 3 — Campo SDK separado para parent-reference + ClOrdID totalmente random

ClOrdID na wire vira random `ulong != 0` (CSPRNG); identidade lógica do
parent algo viaja num campo separado (`SecondaryClOrdID` /
`PartyID` / customizado).

**Pros:**
- Resolve **toda** correlation (cross-session, cross-algo, within-algo).
- Conceptualmente o caminho "correto" — separa identidade de
  agrupamento.

**Cons:**
- **Bloqueado por SDK**: `B3.EntryPoint.Client 0.14.4` não expõe
  `SecondaryClOrdID`, `PartyID`, ou tag custom em `NewOrderRequest`
  (verificado via reflection — apenas
  `ClOrdID/SecurityId/Side/OrderType/Price/StopPrice/OrderQty/TimeInForce/ExpireDate/MaxFloor/MinQty`).
  Para ir por aqui precisamos primeiro abrir issue no SDK e esperar
  release (mesma fila de #356 SDK gaps).
- AdvanceCounterTo deixa de funcionar — não há monotonicidade em random,
  então a única estratégia de WAL replay é "persistir todos os IDs já
  emitidos" (set), o que infla o snapshot ~32B por order viva.
- Collision risk em 2⁶⁴ é matematicamente desprezível (10⁹ orders/dia ⇒
  birthday a 5 × 10⁹ → 10²⁰ orders antes de colisão esperada), mas
  ER routing por igualdade exata significa que **uma** colisão é um
  incidente reportável.
- Re-write substancial dos 5 services + processor + ownership map.

### 4.4 Opção 4 — Random step monotônico (novo)

`counter += stepRandom` onde `stepRandom ∈ [1, MaxStep]` (CSPRNG,
e.g. `MaxStep = 256`). Counter continua estritamente monotônico
crescente. Within-algo: children não são mais contíguos
(`K, K+187, K+331, K+490, …`).

**Pros:**
- **Resolve AC#2**: 100 children consecutivos com step médio 128
  ficam espalhados em ~12.8K-wide janela do counter; sem o segredo do
  step, contraparte vê apenas IDs aparentemente desconexos.
- AdvanceCounterTo **continua válido** — só precisa que o counter
  cresça, não que cresça por +1 (a CAS-loop `current >= counter`
  funciona idêntica).
- Snapshot/Restore **inalterados** — counter persistido é o valor
  atual, restore reproduz; próximo Generate adiciona step random fresco.
- Zero schema bump.

**Cons:**
- Esgotamento do counter cresce ~`MaxStep/2`× — com `MaxStep=256` ⇒ avg
  step 128 ⇒ counter 2⁴⁰ vira 2⁴⁰/128 = 2³³ ≈ 8B IDs por end-client.
  Ainda enorme; > 30 anos a 250M orders/dia.
- **9 testes existentes** que pinam `Assert.Equal(11UL, …)` /
  `Assert.Equal(first + 1UL, second)` / `Assert.Equal(5UL,
  nextAfterReplay & CounterMask)` quebram — precisam virar
  asserts relativos (`Assert.True(second > first)`, `Assert.True(next > prev)`).
- Quebra invariante implícito **`expected = prev + 1`** em
  `PropertyDurabilityTests.cs:379` que computa ClOrdIDs sintéticos para
  WAL replay determinístico — esse test precisa rebuild pra registry-emitted
  IDs em vez de fórmula.

### 4.5 Opção 5 (recomendada) — Hybrid: Opção 2 + Opção 4

Combina:
1. **Random `counter₀`** por (end-client) na primeira allocation
   (Opção 2 — cross-session).
2. **Random step** ∈ [1, 256] em todo `Generate` (Opção 4 — within-algo).

**Pros:**
- Resolve **cross-session** (Opção 2) E **within-algo** (Opção 4) — AC#2
  passa.
- Schema unchanged (counter ainda é `long`).
- AdvanceCounterTo preserved.
- **Não depende do SDK** — implementável já.

**Cons:**
- Mesmo conjunto de breaking tests da Opção 4 (~9 asserts em 2 arquivos).
- **Não** elimina prefixIdx-correlation por end-client (mitigado por
  Opção 1 que fica como Phase 3 futura, dependente de migration de
  `MaxPrefixIndex`).

## 5. Decisão

**Recomendado: Opção 5 (hybrid)** em 3 fases.

| Fase | Escopo                                                                                       | Tracking |
| ---- | -------------------------------------------------------------------------------------------- | -------- |
| 1    | RFC + decisão + entropy-test spec + breaking-test inventory (**este PR**)                    | #449     |
| 2    | Random `counter₀` + random step + atualizar 9 testes + entropy test passing                  | (open)   |
| 3    | Per-(end-client × parentAlgoId) prefix + `MaxPrefixIndex` bump + snapshot format v2          | (open)   |

Fase 2 é o **mínimo** pra fechar a AC do #449. Fase 3 só faz sentido se
observarmos cross-algo correlation em produção (mitigação por defense in
depth contra ataques de fingerprinting de algos institucionais).

Opção 3 (campo SDK separado) fica adiada até SDK >= 0.15 expor o campo;
mesmo aí, Opção 5 não vira "trabalho descartado" porque random-step +
random-base continuam sendo defense in depth.

## 6. Entropy test specification

A AC#2 ("100 child ClOrdIDs de mesmo algo não revelam sequência
contígua") é codificada como:

```csharp
// backend/tests/B3.Trading.Application.Tests/ClOrdIdMaskingTests.cs
[Fact]
public void Generate_HundredConsecutive_HaveNoContiguousRun()
{
    var registry = new ClOrdIdPrefixRegistry();
    var owner = new EndClientId("alice");
    var ids = Enumerable.Range(0, 100).Select(_ => registry.Generate(owner)).ToArray();

    // ── monotonicity (always-true) ──
    for (var i = 1; i < ids.Length; i++)
        Assert.True(ids[i] > ids[i - 1], "ClOrdIDs must be strictly increasing.");

    // ── no contiguous run of length >= K (entropy) ──
    // With random step in [1, 256], probability of two consecutive
    // gaps both being +1 is ≈ (1/256)² ≈ 1.5 × 10⁻⁵; a run of
    // 5 contiguous +1 gaps is < 10⁻¹², so K=5 is a comfortable
    // false-positive bound while still catching a regression to
    // pure +1 incrementing.
    const int kMax = 5;
    var maxRunLen = 1;
    var run = 1;
    for (var i = 1; i < ids.Length; i++)
    {
        run = (ids[i] - ids[i - 1]) == 1UL ? run + 1 : 1;
        maxRunLen = Math.Max(maxRunLen, run);
    }
    Assert.True(maxRunLen < kMax,
        $"100 consecutive ClOrdIDs reveal a contiguous run of length {maxRunLen} — masking regression?");

    // ── starting offset not deterministically 1 (cross-session) ──
    var counter₀ = ids[0] & ClOrdIdPrefixRegistry.CounterMask;
    Assert.True(counter₀ > 1, $"First counter must be randomised (got {counter₀}).");
}
```

Plus a **statistical** test (run 1000 trials, assert that the
distribution of `counter₀ mod 1000` looks uniform with
`χ²` p-value > 0.01 — guards against a RNG regression to
`new Random(seed)` or `Random.Shared.Next()` instead of
`RandomNumberGenerator`).

## 7. Breaking-test inventory (Phase 2)

Arquivos / asserts que precisam virar de "pinned counter value" para
"relative monotonic check":

- `backend/tests/B3.Trading.Application.Tests/ClOrdIdAndOwnershipTests.cs`
  — 9 asserts (linhas 21, 60, 89, 100, 113, 132, 137, 164).
- `backend/tests/B3.Trading.Application.Tests/Persistence/PropertyDurabilityTests.cs:379`
  — formula `((ulong)ownerIdx << CounterBits) | counter` que sintetiza
  IDs para WAL replay determinístico. Substituir por registry-emitted
  IDs e propagar via lookup.
- `backend/tests/B3.Trading.Application.Tests/Orders/PropertyClOrdIdTests.cs`
  — verificar todas as asserts.

Padrão de fix:

```csharp
// antes
var second = registry.Generate(alice);
Assert.Equal(first + 1UL, second);

// depois
var second = registry.Generate(alice);
Assert.True(second > first, "Generate must produce strictly increasing IDs.");
Assert.Equal(first >> ClOrdIdPrefixRegistry.CounterBits,
             second >> ClOrdIdPrefixRegistry.CounterBits); // prefix invariant kept
```

## 8. Migration path

### 8.1 Snapshot back-compat

Schema **inalterado** em Phase 2 — `ClOrdIdCounterSnapshot(string
EndClientId, ulong PrefixIdx, long Counter)` continua igual:

- Snapshot legado escrito com `counter₀ = 0` + steps de +1 restaura como
  `Counter = N`; o próximo `Generate` no processo Phase-2 simplesmente
  faz `Add(stepRandom)`, fica em `N + step` — sempre `> N`, satisfaz
  `AdvanceCounterTo` se algum WAL event observou ID `< N`.
- Snapshot Phase-2 (random base + random step) também tem `Counter = N`
  no momento do snapshot; restore reproduz `N`; próximo Generate
  continua de lá. Idempotente.

Ou seja: **não há "snapshot v1 → v2" migration**. Phase 2 deploy é
rolling-safe: nó velho lê snapshot novo sem problema (apenas counter
mais alto que ele "esperaria"), nó novo lê snapshot velho sem problema
(counter baixo, próximo step random ainda preserva monotonicidade).

### 8.2 WAL replay back-compat

`AdvanceCounterTo(observedClOrdId)`'s CAS loop só checa
`current >= counter`; não assume nada sobre o **gap** entre IDs emitidos.
WAL replay legado (IDs +1) e WAL replay novo (IDs com gaps random)
funcionam idênticos.

### 8.3 Rollout

1. Deploy Phase 2 em todos os nós.
2. Snapshot subsequente captura counters "altos" (random base).
3. Rollback para Phase 1 é seguro: nó Phase 1 lê o counter alto e
   continua de lá com `Increment` de +1 (apenas perde o random-step
   property até o próximo Phase-2 deploy).

### 8.4 Wire-format compat com counterparties / venue

Nenhum impacto — o ClOrdID continua sendo `ulong` non-zero,
strictly monotonic per end-client. A venue não tem expectativa de step
size; o membro contraparte que estava fazendo correlation por step
pattern perde o sinal (que é o ponto).

## 9. Open questions

1. **PRNG choice**: `RandomNumberGenerator.GetInt32(1, 257)` (CSPRNG,
   ~150ns/call) ou `Random.Shared.Next(1, 257)` (non-crypto, ~10ns)?
   Recomendado **CSPRNG** porque o objetivo é defender contra
   contraparte com motivação econômica (HFT player); um PRNG fraco com
   seed leakable virar reverse-engineering target. Latency overhead
   irrelevante (~150ns << ~50µs full submit path).
2. **`MaxStep` tuning**: 256 é compromisso entre entropy (boa proteção
   até ~3 anos worth of children adjacentes) e counter-budget
   (~8B IDs/end-client antes de overflow). Comunidade B3 / counterparty
   profiles produz orders/dia da ordem de 10⁷ por membro tier-1 → 800
   dias = 2 anos antes de overflow. Re-avaliar para `MaxStep = 64` se
   provar limite operacional.
3. **Phase 3 trigger**: implementar per-(endClient × parentAlgoId) prefix
   só se cross-algo correlation virar incidente reportado. Sem
   evidência hoje; deixar como follow-up oportunista.

## 10. Acceptance check vs #449

| AC                                                                       | Status                                                                                                                |
| ------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------- |
| Decisão registrada em RFC                                                | ✅ este documento                                                                                                      |
| Teste de entropia (100 children não contíguos)                           | 📋 spec em §6; implementação em Phase 2 PR                                                                            |
| WAL replay continua funcional                                            | ✅ analisado em §8.2 — AdvanceCounterTo CAS-loop independente de step pattern                                          |
| Migration path documentado                                               | ✅ §8 — snapshot back-compat trivial, rollout rolling-safe, sem wire-format impact                                     |

Phase 2 implementation PR fecha o issue. Phase 3 fica como
defense-in-depth oportunista (sem issue aberto até observarmos cross-algo
correlation em produção).
