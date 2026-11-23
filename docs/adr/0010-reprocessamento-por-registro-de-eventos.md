# ADR-0010 — Reprocessamento do saldo por registro de eventos

Status: Aceita
Data: 2026-11-16

## Contexto

O saldo é uma projeção. É preciso poder reconstruí-lo (RF-08) após um erro, uma correção de regra ou uma divergência detectada na reconciliação (RNF-07). Consultar o serviço de Lançamentos para reconstruir criaria acoplamento síncrono entre os contextos.

## Decisão

O serviço de Consolidação guarda o **registro dos eventos recebidos** (*event log*), na mesma transação que atualiza o `DailyBalance`; a chave primária do registro (`eventId`) é também a garantia de idempotência. O reprocessamento reconstrói o saldo de um dia ou período a partir desse registro, **sem** consultar Lançamentos. Um job de reconciliação compara periodicamente o saldo projetado com a soma do registro e alerta em caso de divergência.

## Alternativas consideradas

| Alternativa | Motivo do descarte |
|---|---|
| Consultar Lançamentos para reconstruir | Reintroduz acoplamento síncrono; quebra o RNF-01 durante o reprocessamento. |
| Saldo gravado sem registro de origem | Não há como reconstruir nem auditar. |
| Event sourcing completo | Mais complexo que o necessário; o registro de eventos no consumidor dá o benefício essencial. |

## Consequências

- (+) Reconstrução e auditoria independentes do serviço de Lançamentos.
- (+) O consolidado continua desacoplado em operação normal e em recuperação.
- (−) Armazenamento adicional; política de retenção e particionamento por data a definir.
- (−) Um evento ausente (perdido antes de chegar) não é recuperável por esse mecanismo; por isso o Outbox e a entrega *at least once* são essenciais.
