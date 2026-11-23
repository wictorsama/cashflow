# ADR-0001 — Dois serviços orientados a eventos com CQRS leve

Status: Aceita
Data: 2026-11-16

## Contexto

O enunciado exige que o controle de lançamentos **não fique indisponível se o consolidado diário cair**, e que o consolidado suporte 50 req/s com perda de até 5%. Há duas capacidades core com perfis diferentes: escrita transacional (lançamentos) e leitura agregada (saldo).

## Decisão

Dois serviços independentes, cada um com seu banco: **Lançamentos (Entries)** como fonte da verdade e **Consolidação (Consolidation)** como projeção de leitura (CQRS leve). A comunicação é assíncrona, por eventos (`EntryRegistered`, `EntryReversed`). Nenhum serviço chama o outro de forma síncrona.

## Alternativas consideradas

| Alternativa | Motivo do descarte |
|---|---|
| Monólito modular | Um processo só cai junto; não comprova fisicamente o isolamento de falha. |
| Chamada síncrona Lançamentos → Consolidação | Acopla a disponibilidade dos dois. |
| Microsserviços granulares (5 ou mais) | Complexidade sem benefício para duas capacidades core. |
| Serverless | Pior para execução local em Kubernetes e para depuração. |

## Consequências

- (+) A queda de um serviço não derruba o outro; escalas independentes.
- (+) Contrato explícito entre contextos (Linguagem Publicada).
- (−) Consistência eventual: o saldo pode estar alguns segundos defasado (RNF-04).
- (−) Mais peças operacionais (broker, cache); mitigado por automação e observabilidade.
