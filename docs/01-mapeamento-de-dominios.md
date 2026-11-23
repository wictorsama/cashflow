# 01 — Mapeamento de Domínios e Capacidades de Negócio

> Convenção: a documentação é em português; os termos de domínio aparecem como *Nome (Name)*, e o código usa apenas o nome em inglês. Ver [Glossário](#7-glossário-linguagem-ubíqua).

## 1. Contexto de negócio

Um comerciante (Merchant) precisa controlar o fluxo de caixa diário por meio de lançamentos (Entries) de débito e crédito, e consultar o saldo diário consolidado (DailyBalance).

## 2. Mapa de capacidades de negócio

| Tipo | Capacidade | Subcapacidades |
|---|---|---|
| Core | **Controle de Fluxo de Caixa (Cash Flow Management)** | Registrar lançamento; estornar lançamento; consultar lançamentos |
| Core | **Consolidação de Saldo (Balance Consolidation)** | Calcular saldo diário; consultar saldo por dia/período; reconstruir saldo (reprocessamento) |
| Suporte | **Gestão de Acesso (Access Management)** | Autenticar; autorizar por papel/escopo; emitir credenciais de serviço |
| Suporte | **Monitoramento Operacional (Operational Monitoring)** | Métricas, traces, logs, alertas; trilha de auditoria |

Capacidade genérica (Access Management): comprar/adotar (ex.: Keycloak), não construir.

## 3. Contextos delimitados (Bounded Contexts) e mapa de contexto

```
 Comerciante ──► [Contexto de Lançamentos (Entries)] ──evento EntryRegistered──► [Contexto de Consolidação (Consolidation)] ◄── Comerciante
                  fonte da verdade (escrita)           Linguagem Publicada       modelo de leitura (consulta)
                                                       (upstream → downstream)

 [Gestão de Acesso (Access Management)] ── tokens JWT ──► ambos
```

- **Lançamentos (Entries)** é *upstream*: dono do lançamento, não conhece o Consolidado. Isso sustenta o requisito não funcional de que o controle de lançamentos não fica indisponível se o consolidado cair.
- **Consolidação (Consolidation)** é *downstream*: conhece apenas o contrato do evento (Linguagem Publicada / Published Language, com esquema versionado).
- Cada contexto possui seu próprio banco de dados; nenhum lê o banco do outro.

## 4. Regras e dados por contexto

| | Lançamentos (Entries) | Consolidação (Consolidation) |
|---|---|---|
| Agregado | `Entry` | `DailyBalance` (por comerciante e dia) |
| Invariantes | valor > 0; tipo válido; imutável após criado | líquido do dia = total de créditos − total de débitos |
| Consistência | forte (transação local + Outbox) | eventual, com meta de atualização (p95 < 5 s, a validar) |
| Emite / consome | emite `EntryRegistered`, `EntryReversed` | consome ambos, de forma idempotente |

## 5. Decisões de modelagem (validadas)

| # | Decisão | Justificativa |
|---|---|---|
| 1 | **Lançamento imutável; correção por estorno (Reversal).** Não há edição nem exclusão; o estorno gera um novo lançamento compensatório. | Padrão contábil; histórico auditável; o Consolidado recebe apenas eventos de acréscimo. |
| 2 | **Multi-comerciante desde o início.** Todo lançamento carrega `merchantId`; um usuário corresponde a um comerciante. | Custo baixo agora; evita remodelagem. |
| 3 | **Duas datas:** `entryDate` (dia de competência, escolhido pelo comerciante) e `registeredAt` (timestamp UTC). O "dia" do saldo segue `entryDate` no fuso do comerciante (padrão `America/Sao_Paulo`). | Separa o fato contábil do fato técnico; evita erros de virada de dia por fuso. |
| 4 | **Lançamento retroativo permitido.** O saldo do dia afetado é recalculado; o saldo acumulado é calculado **na leitura**, não gravado. | Um lançamento retroativo não exige reescrever os dias seguintes. |
| 5 | **Saldo diário consolidado = (a) resultado do dia** (total de créditos, total de débitos, líquido) **e (b) saldo acumulado** até o dia. Saldo inicial assumido como zero. | Atende às duas leituras possíveis do enunciado. |
| 6 | **Moeda única (BRL)**, valor `decimal(18,2)`, nunca ponto flutuante. Multimoeda fica para evoluções futuras. | Simplicidade; precisão monetária. |

## 6. Pontos em aberto

- Meta de frescor do consolidado (p95 < 5 s) a confirmar na etapa de requisitos não funcionais.
- Política de reprocessamento (reconstrução do saldo a partir do histórico de eventos) a detalhar em ADR.

## 7. Glossário (linguagem ubíqua)

| Nome no desenho (docs) | Nome no código |
|---|---|
| Lançamento (Entry) | `Entry` |
| Débito (Debit) / Crédito (Credit) | `Debit` / `Credit` |
| Saldo Diário (DailyBalance) | `DailyBalance` |
| Comerciante (Merchant) | `Merchant` |
| Estorno (Reversal) | `Reversal` |
| Caixa de Saída (Outbox) | `Outbox` |

Termos usuais de mercado (Outbox, CQRS, Gateway) aparecem em inglês, com tradução na primeira ocorrência.
