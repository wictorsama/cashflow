# Registros de Decisões Arquiteturais (ADRs)

Cada decisão relevante tem um registro curto: contexto, **opções com o que cada uma tem a favor e contra**, a decisão e por quê, e quando reavaliar. Formato inspirado em Michael Nygard.

| ADR | Decisão | Status |
|---|---|---|
| [0001](0001-servicos-orientados-a-eventos.md) | Dois serviços orientados a eventos com CQRS leve | Aceita |
| [0002](0002-rabbitmq-como-broker.md) | RabbitMQ como broker e cliente oficial `RabbitMQ.Client` | Aceita |
| [0003](0003-padrao-outbox.md) | Padrão Outbox para publicação de eventos | Aceita |
| [0004](0004-postgresql-por-servico.md) | PostgreSQL, um banco por serviço | Aceita |
| [0005](0005-redis-cache-de-leitura.md) | Redis como cache de leitura com *fallback* | Aceita |
| [0006](0006-keycloak-oidc.md) | Keycloak (OIDC/JWT) para identidade | Aceita |
| [0007](0007-yarp-gateway.md) | YARP como gateway | Aceita |
| [0008](0008-helm-e-kind.md) | Helm e kind para implantação, entrega em camadas | Aceita |
| [0009](0009-observabilidade-opentelemetry.md) | OpenTelemetry com Prometheus, Grafana e Jaeger | Aceita |
| [0010](0010-reprocessamento-por-registro-de-eventos.md) | Reprocessamento do saldo por registro de eventos | Aceita |
| [0011](0011-assistente-de-ia-por-interface.md) | Assistente de IA atrás de uma interface, sem poder de escrita | Aceita (implementada na v1, ver [19](../19-assistente-de-ia.md)) |
| [0012](0012-backend-dotnet.md) | Backend em .NET 10 (C#) | Aceita |
| [0013](0013-frontend-nextjs-bff.md) | Frontend em Next.js, com o servidor chamando a API (BFF) | Aceita |
| [0014](0014-ci-cd-github-actions.md) | CI/CD com GitHub Actions | Aceita |
| [0015](0015-nuvem-kubernetes-portatil.md) | Nuvem: Kubernetes portátil, com a AWS como referência de custo | Aceita |
| [0016](0016-alta-disponibilidade-postgresql-e-broker.md) | Alta disponibilidade: CloudNativePG para o PostgreSQL e *quorum queues* no RabbitMQ | Aceita |
| [0017](0017-trilha-de-auditoria-encadeada.md) | Trilha de auditoria com encadeamento de hashes | Aceita |
| [0018](0018-papel-de-auditor-por-atribuicao.md) | Papel de Auditor: somente leitura, só os comerciantes atribuídos | Aceita |
| [0019](0019-isolamento-de-rede.md) | Isolamento de rede com NetworkPolicy (entrada negada por padrão) | Aceita |
| [0020](0020-reenvio-idempotente-no-gateway.md) | Reenvio automático no Gateway, só do que é seguro repetir | Aceita |
| [0021](0021-idempotencia-por-chave-na-requisicao.md) | Idempotência de escrita por `Idempotency-Key` guardada com o lançamento | Aceita |

## Modelo

```
# ADR-NNNN — Título
Status: Proposta | Aceita | Substituída por ADR-XXXX
Data: AAAA-MM-DD

## Contexto
## Decisão
## Alternativas consideradas
## Consequências
```
