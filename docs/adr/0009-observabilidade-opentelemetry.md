# ADR-0009 — OpenTelemetry com Prometheus, Grafana e Jaeger

Status: Aceita
Data: 2026-11-16

## Contexto

Os SLOs (RNF-03 a RNF-05) precisam ser medidos, e o fluxo assíncrono (escrita → fila → saldo) exige rastreabilidade ponta a ponta. Observabilidade é requisito diferencial do desafio.

## Decisão

- **OpenTelemetry** nos serviços e no Gateway: traces, métricas e logs estruturados, com *correlation ID* propagado inclusive nos cabeçalhos das mensagens.
- **Prometheus** (métricas), **Grafana** (painéis e alertas) e **Jaeger** (traces).
- Painéis de SLO: latência, taxa de erro, vazão, atraso da fila (frescor), tamanho do Outbox, DLQ, taxa de acerto do cache.
- Alertas: taxa de erro, p95 acima da meta, atraso acima da meta de frescor, mensagens na DLQ, divergência na reconciliação.
- *Liveness* e *readiness* distintas.

## Alternativas consideradas

| Alternativa | Motivo do descarte |
|---|---|
| Somente logs | Não permite medir SLO nem seguir um fluxo assíncrono. |
| Solução proprietária (APM comercial) | Custo e dependência de fornecedor; fica como equivalente possível na nuvem. |

## Consequências

- (+) Padrão aberto e portátil; o mesmo código de telemetria serve local e nuvem.
- (+) Permite provar os requisitos com dados (teste de carga com k6 e painéis).
- (−) Consome recursos no ambiente local; a stack é opcional no modo Compose.
