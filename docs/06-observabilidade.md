# 06 — Observabilidade

Implementa o [ADR-0009](adr/0009-observabilidade-opentelemetry.md) e atende ao RNF-09: logs, métricas e traces com correlação, painéis dos SLOs e alertas.

## Visão geral

```text
Gateway ──┐
Entries ──┼── traces (OTLP/gRPC) ──────────▶ Jaeger      (UI: http://localhost:16686)
Consolid. ┘── métricas (OTLP/HTTP) ────────▶ Prometheus ──▶ Grafana (http://localhost:3030)
              logs JSON (stdout, com trace id)               └─ regras de alerta
```

- **Um único protocolo (OpenTelemetry/OTLP)** para traces e métricas: nenhum *scrape* nem coletor intermediário. O Prometheus recebe métricas por OTLP (`--web.enable-otlp-receiver`).
- O código é o mesmo local e na nuvem: só mudam os endereços (`Otel:TracesEndpoint`, `Otel:MetricsEndpoint`). Sem endereço configurado (testes, execução simples) nada é exportado.
- Projeto compartilhado `src/Shared/CashFlow.Observability`: uma chamada configura logs, traces e métricas de qualquer serviço.

## Os três sinais

| Sinal | O que traz | Onde ver |
|---|---|---|
| **Traces** | Requisição HTTP (Gateway → serviço), chamadas ao banco (Npgsql), publicação e consumo no RabbitMQ | Jaeger |
| **Métricas** | Taxa, erros e latência HTTP; métricas de negócio e de fluxo (abaixo); *runtime* .NET | Grafana / Prometheus |
| **Logs** | JSON em *stdout*, com `TraceId` e `SpanId` em cada linha | `kubectl logs`; o `TraceId` leva ao trace |

### Trace ponta a ponta, atravessando a fila

O ponto delicado é o fluxo assíncrono: o registro do lançamento e o cálculo do saldo acontecem em momentos e processos diferentes. Para seguir um lançamento do começo ao fim:

1. Ao gravar o evento no Outbox, o serviço de Lançamentos guarda o **contexto W3C do trace** (`trace_parent`) junto da linha.
2. O *relay* publica a mensagem com o cabeçalho `traceparent`, em um span `entries.events publish` filho da requisição original.
3. A Consolidação lê o cabeçalho e cria o span `entries.events process` como filho do anterior.

Resultado: **um único trace** vai de `POST /v1/entries` no Gateway até a atualização do saldo, mesmo que a Consolidação estivesse fora do ar e processe a mensagem minutos depois. Há teste automatizado que prova isso (`TraceContext_TravelsFromTheRequestThroughTheBrokerToTheConsumer…`).

### Métricas de negócio e de fluxo

| Métrica | Tipo | Responde a |
|---|---|---|
| `cashflow.events.recorded{type}` | contador | Quantos lançamentos/estornos foram gravados |
| `cashflow.outbox.pending` | medidor | Eventos aguardando publicação (cresce se o RabbitMQ cair) |
| `cashflow.outbox.published` / `.publish_delay` | contador / histograma | Vazão e atraso do Outbox |
| `cashflow.queue.depth` | medidor | Fila de consolidação: indicador antecipado de atraso |
| `cashflow.dlq.depth` | medidor | Mensagens que nunca puderam ser processadas |
| `cashflow.events.processed{result}` | contador | `applied`, `duplicate`, `invalid` (DLQ) ou `retry` (voltou à fila) |
| `cashflow.event.freshness` | histograma | **Frescor (RNF-04)**: idade do evento quando o saldo é atualizado |
| `cashflow.cache.requests{result}` | contador | `hit`, `miss` ou `unavailable` (Redis fora) |
| `cashflow.reconciliation.runs{result}` / `.divergences{kind}` / `.last_run_unixtime` | contador / medidor | Se o saldo bate com o que foi registrado e se o job continua rodando ([13](13-reconciliacao.md)) |

## Painel e alertas

O painel **“Cash Flow: SLOs and flow”** é carregado automaticamente no Grafana (arquivo versionado em `deploy/helm/cashflow/files/`). Na primeira linha estão os quatro indicadores que contam a saúde do sistema: taxa de erro na borda, frescor p95 (meta < 15 s), fila e DLQ. Abaixo: vazão por serviço e status, latência p95 (meta < 500 ms), Outbox, resultado do consumo, frescor p50/p95/p99 e uso do *cache*.

Regras de alerta (`prometheus-rules.yml`), cada uma ligada a um requisito:

| Alerta | Condição | Requisito |
|---|---|---|
| `HighErrorRate` | mais de 5% de respostas 5xx no Gateway por 5 min | RNF-05 |
| `SlowRequests` | p95 acima de 500 ms por 10 min | RNF-03 |
| `StaleConsolidation` | frescor p95 acima de 15 s por 5 min | RNF-04 |
| `QueueBacklog` | mais de 1000 mensagens na fila por 5 min | RNF-04 |
| `DeadLetterMessages` | qualquer mensagem na DLQ | RNF-06 |
| `ReconciliationDivergence` | qualquer divergência por mais de 10 min | RNF-07 |
| `ReconciliationStopped` | nenhuma reconciliação concluída há mais de 30 min | RNF-07 |
| `AuditChainBroken` | qualquer problema na cadeia de auditoria por 1 min | RNF-08 ([16](16-trilha-de-auditoria.md)) |
| `AuditVerificationStopped` | nenhuma verificação da cadeia há mais de 30 min | RNF-08 |
| `AssistantUnavailable` | o assistente respondeu "indisponível" a mais de 20% dos pedidos por 10 min | RNF-14 ([19](19-assistente-de-ia.md)) |
| `OutboxStuck` | mais de 100 linhas pendentes por 5 min | RNF-01, RNF-06 |

Localmente os alertas ficam visíveis no Prometheus (aba *Alerts*); o envio a pessoas (Alertmanager, e-mail, chat) é uma evolução para a nuvem.

## *Liveness* e *readiness*

| Rota | Verifica | Usada por |
|---|---|---|
| `/health/live` | só que o processo responde | *livenessProbe* |
| `/health/ready` | o **banco do próprio serviço** | *readinessProbe* |

**Redis e RabbitMQ ficam de fora da *readiness* de propósito.** Sem Redis o serviço lê do banco; sem RabbitMQ o Outbox acumula. Em ambos os casos o serviço continua útil e deve continuar recebendo tráfego; tirá-lo do balanceamento transformaria uma degradação em indisponibilidade, contra o RNF-01.

## Usando

```powershell
./scripts/up.ps1            # sobe tudo, inclusive a observabilidade
```

1. Gere tráfego (ver [05 — Execução local](05-execucao-local.md)).
2. **Grafana** → `http://localhost:3030` (usuário `admin`, senha local descartável `local-dev-only`) → painel *Cash Flow: SLOs and flow*.
3. **Jaeger** → `http://localhost:16686` → serviço `cashflow-gateway` → abra um trace de `POST /v1/entries` e veja os spans do Gateway, do serviço, do banco e, por último, do consumo na Consolidação.

## Limitações atuais

- Jaeger com armazenamento em memória e Prometheus com retenção de 2 dias em disco efêmero: bastam para a demonstração; na nuvem entram armazenamento durável.
- As métricas dos medidores (`outbox.pending`, `queue.depth`) são reportadas por cada réplica; os painéis usam `max` para não duplicar.
- Sem Alertmanager e sem agregação de logs (Loki/ELK): os logs são estruturados e prontos para serem coletados, mas a coleta fica para a nuvem.
- Ainda não há *exemplars* ligando um ponto da métrica ao trace correspondente.
