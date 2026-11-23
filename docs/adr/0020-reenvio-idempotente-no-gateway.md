# ADR-0020 — Reenvio automático no Gateway, só do que é seguro repetir

Status: Aceita
Data: 2026-11-22

## Contexto

Durante o *failover* do banco, 16% das escritas eram recusadas. Repetir a requisição resolve, mas repetir uma escrita pode duplicar um lançamento.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | Sem reenvio (o cliente que lide) | Nenhum risco de duplicar | O comerciante vê o erro; cada cliente precisa implementar repetição |
| **B** | Reenvio no Gateway, **só** leituras e escritas com `Idempotency-Key` | Esconde o *failover* do cliente; seguro, porque a chave faz o serviço devolver o resultado original; configurável | Latência maior no pior caso (~18 s); não ajuda se o próprio Gateway cai |
| **C** | Reenvio de tudo, inclusive escrita sem chave | Maior cobertura | Pode registrar o mesmo lançamento duas vezes |
| **D** | Reenvio por malha de serviço | Sem código no Gateway | Exige a malha (ver ADR-0019); mesma lógica de segurança a decidir |

## Decisão e por quê

**B.** Medido: escritas recusadas durante a falha do banco caíram de **16,0% para 4,4%** (e 0,5% na medição final). A regra de ouro é que escrita sem chave **nunca** é repetida. Ver [14](../14-alta-disponibilidade.md#reenvio-de-escrita-no-gateway).

## Quando reavaliar

Em nuvem, o balanceador com *health check* resolve a perda de nó que o reenvio não cobre; manter o reenvio para o *failover* do banco.
